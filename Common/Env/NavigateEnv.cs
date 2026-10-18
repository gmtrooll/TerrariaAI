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
    /// Learner to server: per bot, the current and the next scenario (each 5 x i32: start x, start y, target x,
    ///   target y, max decisions). Then the first step packet follows.
    /// Step packet, server to learner: tick u32; per bot: flags u8, path_distance f32, observation bytes; then for
    ///   every bot whose episode ended (flags & DONE), in bot order: terminal path_distance f32 + terminal
    ///   observation bytes. Flags: 1 done, 2 win, 4 died, 8 timeout, 16 new episode started (this observation is
    ///   the first one of it).
    /// Learner to server: per bot: horizontal u8 (0 left, 1 none, 2 right), vertical u8 (0 up, 1 none, 2 down),
    ///   jump u8 (0/1), has_next u8, next scenario 5 x i32 (read always, used when has_next = 1).
    /// When an episode ends the bot is moved to its queued next scenario in the same tick, so the learner must
    /// always keep one queued per bot.
    /// </summary>
    public class NavigateEnv : ModSystem
    {
        public const uint Magic = 0x31564E54;  // "TNV1"
        public const int Version = 1;
        public const int ScenarioInts = 5;
        public const int CommandBytes = 4 + ScenarioInts * 4;
        public const byte Done = 1, Win = 2, Died = 4, Timeout = 8, NewEpisode = 16;
        public const int FieldRadius = 130, MaxJumpTiles = 13, MaxAirGapTiles = 5;
        public const float GoalRadiusTiles = 1.5f, OffPathPenaltyTiles = 10f;
        /// <summary>The observation's waypoint is this many path tiles ahead of the bot (inside the 41 x 41 grid).</summary>
        public const int WaypointSteps = 12;

        public static readonly int Port = AutomationArgs.GetIntArg("-env", 0);
        public static readonly int FrameSkip = AutomationArgs.GetIntArg("-frameskip", 4);
        public static bool Active;

        private sealed class Scenario
        {
            public int StartX, StartY, TargetX, TargetY, MaxSteps;
        }

        private sealed class BotEnv
        {
            public int Slot;
            public Scenario Current, Next;
            public PathDistanceField Field;
            public int Steps;
            public byte Horizontal = 1, Vertical = 1, Jump;
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
            foreach (BotEnv b in bots) StartEpisode(b, b.Current);

            int obsBytes = ObservationBuilder.TotalBytes;
            sendBuffer = new byte[4 + bots.Length * (5 + obsBytes) + bots.Length * (4 + obsBytes)];
            obsScratch = new byte[obsBytes];
            Active = true;
            tickInDecision = 0;
            // Run ticks back to back, as fast as the learner answers.
            BridgeSystem.Paused = true;
            BridgeSystem.StepsLeft = int.MaxValue / 2;
            SendStep(new bool[bots.Length], new byte[bots.Length], new float[bots.Length]);
            ReadCommands();
            ModContent.GetInstance<NavigateEnv>().Mod.Logger.Info($"[Env] connected to learner on {Port}: {bots.Length} bots, {obsBytes} observation bytes");
        }

        private static Scenario ReadScenario() {
            return new Scenario {
                StartX = reader.ReadInt32(), StartY = reader.ReadInt32(),
                TargetX = reader.ReadInt32(), TargetY = reader.ReadInt32(), MaxSteps = reader.ReadInt32(),
            };
        }

        // ---------- episodes ----------

        private static void StartEpisode(BotEnv b, Scenario s) {
            Player p = Main.player[b.Slot];
            if (p.dead) {
                p.dead = false;
                p.Spawn(PlayerSpawnContext.ReviveFromDeath);
            }
            p.position = new Vector2(s.StartX * 16 + 8 - p.width / 2f, (s.StartY + 1) * 16 - p.height);
            p.velocity = Vector2.Zero;
            p.fallStart = p.fallStart2 = (int)(p.position.Y / 16f);
            p.statLife = p.statLifeMax2;
            p.immuneTime = 0;
            p.jump = 0;
            p.breath = p.breathMax;
            var op = p.GetModPlayer<ObservationPlayer>();
            // Target point: body centre of a player standing on the target cell (as in PathDistanceField's goals).
            op.Target = new Vector2((s.TargetX + 0.5f) * 16f, (s.TargetY - 0.3f) * 16f);
            op.Waypoint = null;
            op.SeenBits = null;
            op.CoarseCache = null;
            b.Current = s;
            b.Field = GetField(s.TargetX, s.TargetY);
            b.Steps = 0;
            b.Horizontal = 1;
            b.Vertical = 1;
            b.Jump = 0;
        }

        private static PathDistanceField GetField(int tx, int ty) {
            long key = ((long)tx << 32) | (uint)ty;
            if (fields.TryGetValue(key, out PathDistanceField field)) return field;
            if (fields.Count > 512) fields.Clear();
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            fieldBuilds++;
            field = PathDistanceField.Build(GridSensors.ClassifyPathTile, tx + 0.5f, ty - 0.3f, FieldRadius, MaxJumpTiles, GoalRadiusTiles, MaxAirGapTiles);
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
                Player p = Main.player[b.Slot];
                float dist = PathDistance(b);
                byte f = 0;
                if (p.dead) f |= Died;
                else if (dist == 0) f |= Win;
                else if (b.Steps >= b.Current.MaxSteps) f |= Timeout;
                if (f == 0) continue;
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
    }
}
