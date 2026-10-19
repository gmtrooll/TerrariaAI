using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using FirstMod_Sp.Common.Automation;
using FirstMod_Sp.Common.Benchmark;
using FirstMod_Sp.Common.Observation;
using FirstMod_Sp.Common.Systems;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Terraria;
using Terraria.ID;
using Terraria.DataStructures;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Env
{
    /// <summary>
    /// Navigate training environment (protocol "TNV1"), active with -env PORT. The learner listens on PORT; this
    /// server connects once its bots exist, then runs lock-step: every FrameSkip ticks it reports every bot's
    /// observation and episode events, and waits for the next actions.
    ///
    /// Handshake, server to learner: magic u32, version i32, bots i32, frame_skip i32, obs_bytes i32,
    ///   spec_len i32 + spec UTF-8 JSON (observation layout, names, class table, world name).
    /// Learner to server: per bot, the current and the next scenario (each 6 x i32: start x, start y, target x,
    ///   target y, max decisions, loadout). Then the first step packet follows. Loadout: 0 = no extra jump,
    ///   1 = Tsunami in a Bottle; the bot wears it for the episode and the route is planned for its jump height.
    ///   (Version 1 had no loadout field; the learner reads the version from the handshake.)
    /// Step packet, server to learner: tick u32; per bot: flags u8, path_distance f32, observation bytes; then for
    ///   every bot whose episode ended (flags & DONE), in bot order: terminal path_distance f32 + terminal
    ///   observation bytes. Flags: 1 done, 2 win, 4 died, 8 timeout, 16 new episode started (this observation is
    ///   the first one of it).
    /// Learner to server: per bot: horizontal u8 (0 left, 1 none, 2 right), vertical u8 (0 up, 1 none, 2 down),
    ///   jump u8 (0/1), has_next u8, next scenario 6 x i32 (read always, used when has_next = 1).
    /// When an episode ends the bot is moved to its queued next scenario in the same tick, so the learner must
    /// always keep one queued per bot.
    ///
    /// Watching (navigate.watch): -envrealtime keeps the game at normal speed instead of running ticks back to back.
    /// A scenario with start x &lt; 0 starts where the bot is (no teleport); target x &lt; 0 means "follow the local
    /// player" (the target moves with them). On a client the camera follows one bot (-envcamera N, keys 1-9 switch,
    /// 0 = own player), markers show every bot's target (yellow) and waypoint (blue), and a status line is drawn.
    /// </summary>
    public class NavigateEnv : ModSystem
    {
        public const uint Magic = 0x31564E54;  // "TNV1"
        public const int Version = 2;
        public const int ScenarioInts = 6;
        public const int CommandBytes = 4 + ScenarioInts * 4;
        public const byte Done = 1, Win = 2, Died = 4, Timeout = 8, NewEpisode = 16;
        public const int FieldRadius = 130, MaxJumpTiles = 13, MaxAirGapTiles = 5, MaxSafeFallTiles = 25;
        public const int LoadoutNone = 0, LoadoutTsunami = 1;

        /// <summary>Planner jump height per loadout: measured apex 6.13 tiles for one jump, 13.12 with Tsunami.</summary>
        public static int JumpTilesFor(int loadout) => loadout == LoadoutTsunami ? MaxJumpTiles : 6;
        public const float GoalRadiusTiles = 1.5f, OffPathPenaltyTiles = 10f;
        /// <summary>The observation's waypoint is this many path tiles ahead of the bot (inside the 41 x 41 grid).</summary>
        public const int WaypointSteps = 12, NearWaypointSteps = 3;

        public static readonly int Port = AutomationArgs.GetIntArg("-env", 0);
        public static readonly int FrameSkip = AutomationArgs.GetIntArg("-frameskip", 4);
        public static readonly bool RealTime = Program.LaunchParameters.ContainsKey("-envrealtime");
        /// <summary>-envrecord PREFIX: append every failed episode (bot path + planned route) as a JSON line to PREFIX_world.jsonl.</summary>
        public static readonly string RecordPath = Program.LaunchParameters.TryGetValue("-envrecord", out string rp) ? rp : null;
        private static StreamWriter recordWriter;
        public static bool Active;

        private sealed class Scenario
        {
            public int StartX, StartY, TargetX, TargetY, MaxSteps, Loadout = LoadoutTsunami;
        }

        private sealed class BotEnv
        {
            public int Slot;
            public Scenario Current, Next;
            public PathDistanceField Field;
            public int Steps;
            public byte Horizontal = 1, Vertical = 1, Jump;
            public int FollowX = int.MinValue, FollowY;
            public int Wins, Deaths, Timeouts;
            public string LastResult = "";
            public List<int[]> Trajectory = new List<int[]>();
        }

        private static BotEnv[] bots = Array.Empty<BotEnv>();
        private static TcpClient client;
        private static BinaryReader reader;
        private static Stream stream;
        private static byte[] sendBuffer = Array.Empty<byte>();
        private static byte[] obsScratch = Array.Empty<byte>();
        private static readonly Dictionary<long, PathDistanceField> fields = new Dictionary<long, PathDistanceField>();
        private static int tickInDecision;

        // Time per decision, in Stopwatch ticks (env_stats bridge command).
        private static long timeFields, timeObservations, timeSend, timeWait, timeTotal, decisions, fieldBuilds;
        private static int connectCooldown;

        public override void Load() {
            if (AutomationArgs.Enabled) BridgeCommands.Register("env_stats", r => {
                double Ms(long t) => decisions > 0 ? Math.Round(t * 1000.0 / System.Diagnostics.Stopwatch.Frequency / decisions, 3) : 0;
                r.Reply(new { active = Active, bots = bots.Length, decisions, field_builds = fieldBuilds, cached_fields = fields.Count,
                    ms_per_decision = new { fields = Ms(timeFields), observations = Ms(timeObservations), send = Ms(timeSend), wait_for_learner = Ms(timeWait), total_in_env = Ms(timeTotal) } });
                if (r.Args.Value<bool?>("reset") == true) timeFields = timeObservations = timeSend = timeWait = timeTotal = decisions = fieldBuilds = 0;
            });
        }

        public override void OnWorldUnload() {
            Disconnect();
        }

        public override void Unload() {
            Disconnect();
            fields.Clear();
        }

        private static void Disconnect() {
            Active = false;
            try {
                client?.Close();
            }
            catch (Exception) { }
            client = null;
            reader = null;
            stream = null;
        }

        public override void PreUpdatePlayers() {
            if (!Active) return;
            foreach (BotEnv b in bots) {
                if (!BotManagerSystem.BotStates.TryGetValue(b.Slot, out BotData data)) continue;
                data.ControlLeft = b.Horizontal == 0;
                data.ControlRight = b.Horizontal == 2;
                data.ControlUp = b.Vertical == 0;
                data.ControlDown = b.Vertical == 2;
                data.ControlJump = b.Jump == 1;
            }
        }

        public override void PostUpdateEverything() {
            if (Port <= 0 || Main.gameMenu) return;
            if (!Active) {
                TryConnect();
                return;
            }
            if (++tickInDecision < FrameSkip) return;
            tickInDecision = 0;
            try {
                Decide();
            }
            catch (IOException e) {
                ModContent.GetInstance<NavigateEnv>().Mod.Logger.Warn("[Env] learner connection lost: " + e.Message);
                Disconnect();
                // A training server without its learner has nothing to do; left running it holds memory and the mod
                // file (orphans after a learner crash). Worlds are never saved in environment mode, so exit at once.
                if (Main.dedServ) Environment.Exit(0);
                BridgeSystem.Paused = false;
                BridgeSystem.StepsLeft = 0;
            }
        }

        // ---------- connection ----------

        private static void TryConnect() {
            if (BotManagerSystem.ActiveBots.Count == 0 || --connectCooldown > 0) return;
            connectCooldown = 120;
            var attempt = new TcpClient { NoDelay = true, ReceiveBufferSize = 1 << 20, SendBufferSize = 1 << 22 };
            try {
                if (!attempt.ConnectAsync("127.0.0.1", Port).Wait(200)) {
                    attempt.Close();
                    return;
                }
            }
            catch (Exception) {
                attempt.Close();
                return;
            }
            client = attempt;
            stream = new BufferedStream(client.GetStream(), 1 << 20);
            reader = new BinaryReader(stream);
            Handshake();
        }

        private static void Handshake() {
            bots = new BotEnv[BotManagerSystem.ActiveBots.Count];
            for (int i = 0; i < bots.Length; i++) bots[i] = new BotEnv { Slot = BotManagerSystem.ActiveBots[i] };

            var spec = new {
                world = Main.worldName,
                observation = ObservationSystem.Spec(includeClasses: true),
                frame_skip = FrameSkip,
                fidelity = Fidelity.FidelitySystem.State(),
                bot_lean = Bots.BotLeanUpdate.Enabled,
            };
            byte[] specBytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(JsonOut.ToToken(spec)));
            var w = new BinaryWriter(stream);
            w.Write(Magic);
            w.Write(Version);
            w.Write(bots.Length);
            w.Write(FrameSkip);
            w.Write(ObservationBuilder.TotalBytes);
            w.Write(specBytes.Length);
            w.Write(specBytes);
            w.Flush();

            foreach (BotEnv b in bots) {
                b.Current = ReadScenario();
                b.Next = ReadScenario();
            }

            // Navigate scenarios run without enemies (a scenario choice, not a fidelity switch): clear them once;
            // the learner launches servers with npc_spawns off.
            BenchIsolation.ClearWorld();
            WorldIntegrity.Snapshot();
            foreach (BotEnv b in bots) StartEpisode(b, b.Current);

            int obsBytes = ObservationBuilder.TotalBytes;
            sendBuffer = new byte[4 + bots.Length * (5 + obsBytes) + bots.Length * (4 + obsBytes)];
            obsScratch = new byte[obsBytes];
            Active = true;
            tickInDecision = 0;
            // Run ticks back to back, as fast as the learner answers (unless watching in real time).
            if (!RealTime) {
                BridgeSystem.Paused = true;
                BridgeSystem.StepsLeft = int.MaxValue / 2;
            }
            SendStep(new bool[bots.Length], new byte[bots.Length], new float[bots.Length]);
            ReadCommands();
            ModContent.GetInstance<NavigateEnv>().Mod.Logger.Info($"[Env] connected to learner on {Port}: {bots.Length} bots, {obsBytes} observation bytes");
        }

        private static Scenario ReadScenario() {
            return new Scenario {
                StartX = reader.ReadInt32(), StartY = reader.ReadInt32(),
                TargetX = reader.ReadInt32(), TargetY = reader.ReadInt32(), MaxSteps = reader.ReadInt32(),
                Loadout = reader.ReadInt32(),
            };
        }

        // ---------- episodes ----------

        private static void StartEpisode(BotEnv b, Scenario s) {
            Player p = Main.player[b.Slot];
            if (p.dead) {
                p.dead = false;
                p.Spawn(PlayerSpawnContext.ReviveFromDeath);
            }
            if (s.StartX >= 0) {
                p.position = new Vector2(s.StartX * 16 + 8 - p.width / 2f, (s.StartY + 1) * 16 - p.height);
                p.velocity = Vector2.Zero;
                p.fallStart = p.fallStart2 = (int)(p.position.Y / 16f);
            }
            p.statLife = p.statLifeMax2;
            p.immuneTime = 0;
            p.jump = 0;
            p.breath = p.breathMax;
            var op = p.GetModPlayer<ObservationPlayer>();
            // Target point: body centre of a player standing on the target cell (as in PathDistanceField's goals).
            op.Waypoint = null;
            op.NearWaypoint = null;
            op.SeenBits = null;
            op.CoarseCache = null;
            b.Current = s;
            b.Trajectory.Clear();
            // Equipment for this episode (accessory slot 3, as BotManagerSystem gives it at spawn).
            int accessory = s.Loadout == LoadoutTsunami ? ItemID.TsunamiInABottle : ItemID.None;
            if (p.armor[3].type != accessory) p.armor[3].SetDefaults(accessory);
            WorldIntegrity.Episode[b.Slot]++;  // changes this bot made in its last episode may now be restored
            if (s.TargetX >= 0) {
                op.Target = new Vector2((s.TargetX + 0.5f) * 16f, (s.TargetY - 0.3f) * 16f);
                b.Field = GetField(s.TargetX, s.TargetY, JumpTilesFor(s.Loadout));
            }
            else {
                b.FollowX = int.MinValue;
                UpdateFollowTarget(b);
            }
            b.Steps = 0;
            b.Horizontal = 1;
            b.Vertical = 1;
            b.Jump = 0;
        }

        /// <summary>Follow scenarios: the target is the local player's feet cell, re-planned when they move.</summary>
        private static void UpdateFollowTarget(BotEnv b) {
            if (Main.dedServ || Main.myPlayer < 0 || Main.myPlayer >= Main.maxPlayers) return;
            Player me = Main.LocalPlayer;
            int tx = (int)(me.Center.X / 16f), ty = (int)((me.Bottom.Y - 1f) / 16f);
            if (tx == b.FollowX && ty == b.FollowY && b.Field != null) return;
            b.FollowX = tx;
            b.FollowY = ty;
            b.Field = GetField(tx, ty, JumpTilesFor(b.Current?.Loadout ?? LoadoutTsunami));
            Main.player[b.Slot].GetModPlayer<ObservationPlayer>().Target = new Vector2((tx + 0.5f) * 16f, (ty - 0.3f) * 16f);
        }

        private static void Record(BotEnv b, string result) {
            recordWriter ??= new StreamWriter($"{RecordPath}_{Main.worldName}.jsonl", append: true) { AutoFlush = true };
            var route = new List<int[]> { new[] { b.Current.StartX, b.Current.StartY } };
            int x = b.Current.StartX, y = b.Current.StartY;
            while (route.Count < 2000 && b.Field.Waypoint(x, y, 1, out int nx, out int ny) && (nx != x || ny != y)) {
                x = nx;
                y = ny;
                route.Add(new[] { x, y });
            }
            recordWriter.WriteLine(JsonConvert.SerializeObject(new {
                world = Main.worldName, result, steps = b.Steps, start = new[] { b.Current.StartX, b.Current.StartY },
                target = new[] { b.Current.TargetX, b.Current.TargetY }, start_distance = b.Field.GetDistance(b.Current.StartX, b.Current.StartY),
                trajectory = b.Trajectory, route,
            }));
        }

        private static int faultsLogged;

        /// <summary>The player's position is a number and inside the world (with a margin for the world edge).</summary>
        private static bool InsideWorld(Player p) {
            return float.IsFinite(p.position.X) && float.IsFinite(p.position.Y) && float.IsFinite(p.velocity.X) && float.IsFinite(p.velocity.Y)
                && p.position.X > 16f * 2 && p.position.X < (Main.maxTilesX - 3) * 16f && p.position.Y > 16f * 2 && p.position.Y < (Main.maxTilesY - 4) * 16f;
        }

        private static PathDistanceField GetField(int tx, int ty, int jumpTiles) {
            long key = ((long)tx << 34) | ((long)(uint)ty << 6) | (uint)(jumpTiles & 63);
            if (fields.TryGetValue(key, out PathDistanceField field)) return field;
            if (fields.Count > 512) fields.Clear();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            fieldBuilds++;
            field = PathDistanceField.Build(GridSensors.ClassifyPathTile, tx + 0.5f, ty - 0.3f, FieldRadius, jumpTiles, GoalRadiusTiles, MaxAirGapTiles,
                GridSensors.PathInfo, MaxSafeFallTiles);
            fields[key] = field;
            timeFields += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            return field;
        }

        /// <summary>Path distance in tiles from the bot's feet (as RLNetworkSystem.PathDistanceTiles), or the straight
        /// distance plus a penalty when the bot is outside the field or cut off.</summary>
        private static float PathDistance(BotEnv b) {
            Player p = Main.player[b.Slot];
            int tx = (int)(p.Center.X / 16f), ty = (int)((p.Bottom.Y - 1f) / 16f);
            int d = b.Field.GetDistance(tx, ty);
            if (d == PathDistanceField.UNREACHABLE) d = b.Field.GetDistance(tx, ty - 1);
            if (d != PathDistanceField.UNREACHABLE) return d;
            return Vector2.Distance(p.Center, Main.player[b.Slot].GetModPlayer<ObservationPlayer>().Target ?? p.Center) / 16f + OffPathPenaltyTiles;
        }

        /// <summary>Sets the bot's observation waypoint from its route (the planner's distance field).</summary>
        private static void UpdateWaypoint(BotEnv b) {
            Player p = Main.player[b.Slot];
            var op = p.GetModPlayer<ObservationPlayer>();
            int tx = (int)(p.Center.X / 16f), ty = (int)((p.Bottom.Y - 1f) / 16f);
            op.Waypoint = ObservationConfig.Waypoint && b.Field.Waypoint(tx, ty, WaypointSteps, out int wx, out int wy)
                ? new Vector2((wx + 0.5f) * 16f, (wy - 0.3f) * 16f) : null;
            op.NearWaypoint = ObservationConfig.NearWaypoint && b.Field.Waypoint(tx, ty, NearWaypointSteps, out int nx, out int ny)
                ? new Vector2((nx + 0.5f) * 16f, (ny - 0.3f) * 16f) : null;
        }

        private static void Decide() {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            decisions++;
            int n = bots.Length;
            var done = new bool[n];
            var flags = new byte[n];
            var terminalDistance = new float[n];
            int obsBytes = ObservationBuilder.TotalBytes;
            int doneCount = 0;
            for (int i = 0; i < n; i++) {
                BotEnv b = bots[i];
                b.Steps++;
                if (b.Current.TargetX < 0) UpdateFollowTarget(b);
                Player p = Main.player[b.Slot];
                bool faulted = BotManagerSystem.Faulted.Remove(b.Slot) || !InsideWorld(p);
                if (faulted) {
                    if (faultsLogged++ < 20)
                        ModContent.GetInstance<NavigateEnv>().Mod.Logger.Warn($"[Env] bot {b.Slot} faulted (position {p.position}, velocity {p.velocity}) in scenario " +
                            $"({b.Current.StartX},{b.Current.StartY}) -> ({b.Current.TargetX},{b.Current.TargetY}) after {b.Steps} decisions; episode ended as a death");
                    // Back to a valid place before anything reads the position (the terminal observation indexes tiles).
                    p.position = b.Current.StartX >= 0
                        ? new Vector2(b.Current.StartX * 16 + 8 - p.width / 2f, (b.Current.StartY + 1) * 16 - p.height)
                        : new Vector2(Main.spawnTileX * 16f, (Main.spawnTileY - 3) * 16f);
                    p.velocity = Vector2.Zero;
                }
                WorldIntegrity.MarkAround(p);
                if (RecordPath != null) b.Trajectory.Add(new[] { (int)(p.Center.X / 16f), (int)((p.Bottom.Y - 1f) / 16f) });
                float dist = PathDistance(b);
                byte f = 0;
                if (p.dead || faulted) f |= Died;
                else if (dist == 0) f |= Win;
                else if (b.Steps >= b.Current.MaxSteps) f |= Timeout;
                if (f == 0) continue;
                if ((f & Win) != 0) b.Wins++;
                else if ((f & Died) != 0) b.Deaths++;
                else b.Timeouts++;
                b.LastResult = (f & Win) != 0 ? "reached" : (f & Died) != 0 ? "died" : "timed out";
                if (RecordPath != null && (f & Win) == 0) Record(b, b.LastResult);
                done[i] = true;
                flags[i] = (byte)(f | Done | NewEpisode);
                terminalDistance[i] = dist;
                doneCount++;
            }
            // Terminal observations before the resets.
            var terminalObs = doneCount > 0 ? new byte[doneCount * obsBytes] : Array.Empty<byte>();
            for (int i = 0, k = 0; i < n; i++) {
                if (!done[i]) continue;
                UpdateWaypoint(bots[i]);
                ObservationBuilder.Build(Main.player[bots[i].Slot], obsScratch);
                Buffer.BlockCopy(obsScratch, 0, terminalObs, k * obsBytes, obsBytes);
                k++;
            }
            for (int i = 0; i < n; i++) {
                if (!done[i]) continue;
                BotEnv b = bots[i];
                StartEpisode(b, b.Next ?? b.Current);
                b.Next = null;
            }
            SendStep(done, flags, terminalDistance, terminalObs);
            long waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
            ReadCommands();
            long end = System.Diagnostics.Stopwatch.GetTimestamp();
            timeWait += end - waitStart;
            timeTotal += end - start;
        }

        private static void SendStep(bool[] done, byte[] flags, float[] terminalDistance, byte[] terminalObs = null) {
            int obsBytes = ObservationBuilder.TotalBytes;
            int o = 0;
            BitConverter.TryWriteBytes(sendBuffer.AsSpan(o, 4), Main.GameUpdateCount);
            o += 4;
            for (int i = 0; i < bots.Length; i++) {
                BotEnv b = bots[i];
                sendBuffer[o++] = flags[i];
                BitConverter.TryWriteBytes(sendBuffer.AsSpan(o, 4), PathDistance(b));
                o += 4;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                UpdateWaypoint(b);
                ObservationBuilder.Build(Main.player[b.Slot], sendBuffer.AsSpan(o, obsBytes));
                timeObservations += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                o += obsBytes;
            }
            for (int i = 0, k = 0; i < bots.Length; i++) {
                if (!done[i]) continue;
                BitConverter.TryWriteBytes(sendBuffer.AsSpan(o, 4), terminalDistance[i]);
                o += 4;
                Buffer.BlockCopy(terminalObs, k * obsBytes, sendBuffer, o, obsBytes);
                o += obsBytes;
                k++;
            }
            long sendStart = System.Diagnostics.Stopwatch.GetTimestamp();
            stream.Write(sendBuffer, 0, o);
            stream.Flush();
            timeSend += System.Diagnostics.Stopwatch.GetTimestamp() - sendStart;
        }

        private static void ReadCommands() {
            foreach (BotEnv b in bots) {
                b.Horizontal = reader.ReadByte();
                b.Vertical = reader.ReadByte();
                b.Jump = reader.ReadByte();
                bool hasNext = reader.ReadByte() != 0;
                Scenario next = ReadScenario();
                if (hasNext) b.Next = next;
            }
        }

        // ---------- watching (client) ----------

        private static int cameraBot = AutomationArgs.GetIntArg("-envcamera", 0);
        private static readonly bool[] keyWasDown = new bool[10];

        private static bool Watching => Active && !Main.dedServ && !Main.gameMenu;

        public override void PostUpdateInput() {
            if (!Watching || !Main.hasFocus || Main.drawingPlayerChat || Main.editSign || Main.editChest) return;
            for (int k = 0; k <= 9; k++) {
                bool down = Main.keyState.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.D0 + k);
                if (down && !keyWasDown[k]) cameraBot = k == 0 ? -1 : Math.Min(k - 1, bots.Length - 1);
                keyWasDown[k] = down;
            }
        }

        public override void PostUpdateDusts() {
            if (!Watching) return;
            // Light the whole view so caves are visible to the viewer. Display only: the bots' observation computes
            // its own visibility and never reads the game's lighting.
            Vector2 view = new Vector2(Main.screenWidth, Main.screenHeight) / Main.GameViewMatrix.Zoom;
            Vector2 center = Main.screenPosition + new Vector2(Main.screenWidth, Main.screenHeight) / 2f;
            for (float x = center.X - view.X / 2 - 96; x <= center.X + view.X / 2 + 96; x += 40) {
                for (float y = center.Y - view.Y / 2 - 96; y <= center.Y + view.Y / 2 + 96; y += 40) Lighting.AddLight(new Vector2(x, y), 1f, 1f, 1f);
            }
            if (Main.GameUpdateCount % 3 != 0) return;
            foreach (BotEnv b in bots) {
                var op = Main.player[b.Slot].GetModPlayer<ObservationPlayer>();
                if (op.Target is Vector2 t && b.Current != null && b.Current.TargetX >= 0) Marker(t, Terraria.ID.DustID.GemTopaz, 1.4f);
                if (op.Waypoint is Vector2 w) Marker(w, Terraria.ID.DustID.GemSapphire, 1.0f);
                if (op.NearWaypoint is Vector2 nw) Marker(nw, Terraria.ID.DustID.GemEmerald, 0.8f);
            }
        }

        private static void Marker(Vector2 at, int type, float scale) {
            Dust d = Dust.NewDustPerfect(at, type, Vector2.Zero, 0, default, scale);
            d.noGravity = true;
        }

        public override void ModifyScreenPosition() {
            if (!Watching || cameraBot < 0 || cameraBot >= bots.Length) return;
            Player p = Main.player[bots[cameraBot].Slot];
            Main.screenPosition = p.Center - new Vector2(Main.screenWidth, Main.screenHeight) / 2f;
        }

        public override void PostDrawInterface(Microsoft.Xna.Framework.Graphics.SpriteBatch spriteBatch) {
            if (!Watching) return;
            int wins = 0, deaths = 0, timeouts = 0;
            foreach (BotEnv b in bots) {
                wins += b.Wins;
                deaths += b.Deaths;
                timeouts += b.Timeouts;
            }
            int total = wins + deaths + timeouts;
            var lines = new List<string> {
                $"Navigate: {bots.Length} bots   |   reached {wins}, died {deaths}, timed out {timeouts}" + (total > 0 ? $"  ({100 * wins / total}% success)" : ""),
                "Keys 1-9: follow a bot   0: your own player   (yellow = target, blue = route waypoint)",
            };
            if (cameraBot >= 0 && cameraBot < bots.Length) {
                BotEnv b = bots[cameraBot];
                string limit = b.Current.MaxSteps > 100000 ? "" : $"/{b.Current.MaxSteps}";
                lines.Add($"Watching bot {cameraBot + 1}: decision {b.Steps}{limit}, {PathDistance(b):0} tiles of route left"
                          + (b.LastResult.Length > 0 ? $"   |   last episode: {b.LastResult}" : ""));
            }
            float y = 110;
            foreach (string line in lines) {
                Utils.DrawBorderString(spriteBatch, line, new Vector2(Main.screenWidth / 2f, y), Color.White, 0.9f, 0.5f);
                y += 24;
            }
        }
    }
}
