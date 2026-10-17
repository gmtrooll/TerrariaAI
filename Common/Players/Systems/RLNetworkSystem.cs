using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using Terraria;
using Terraria.ModLoader;
using Microsoft.Xna.Framework;
using FirstMod_Sp.Common.Automation;
using FirstMod_Sp.Content.Projectiles;

namespace FirstMod_Sp.Common.Systems
{
    /// <summary>
    /// Lock-step bridge to the Python trainer (see RLProtocol for the byte layout).
    ///
    /// Timing of one decision tick (runs after BotManagerSystem.PostUpdatePlayers, which already
    /// moved the bots with the previous action):
    ///   1. end episodes (win / death / timeout / abort), capture their terminal observation,
    ///      and move those bots into their queued zone, all before the packet is built,
    ///   2. send observations of the (possibly new) state, path distances and flags,
    ///   3. read one action per bot and apply it for the next FRAME_SKIP ticks.
    /// So the observation Python receives after sending action a_t is the state after a_t.
    /// </summary>
    public class RLNetworkSystem : ModSystem
    {
        private const int RECONNECT_INTERVAL = 120; // ~2 seconds at 60fps
        private const int HANDSHAKE_TIMEOUT_MS = 5000;
        private const int MAX_HANDSHAKE_MESSAGE_BYTES = 4096;
        private const float WIN_RADIUS_TILES = 2f;
        // Only matters without zones (target marker mode): the bot stays on the target after a win,
        // so wait this many decisions before another win can be reported.
        private const int WIN_COOLDOWN_STEPS = 100;
        private const int MAX_CACHED_PATH_FIELDS = 64;
        private const int DEBUG_LOG_INTERVAL = 100;

        private TcpClient client;
        private NetworkStream stream;
        private BinaryWriter writer;
        private BinaryReader reader;
        private bool connectFailureReported = false;

        // CONFIG VALUES (loaded from training_config.json)
        private int BATCH_SIZE;
        private int MAX_STEPS_PER_ZONE;
        private int FRAME_SKIP;
        private bool DEBUG;

        private byte[] sendBuffer;
        private byte[] terminalBuffer;
        private readonly float[] emptyStats = new float[RLProtocol.STATS_SIZE];
        private readonly float[] emptyGrid = new float[RLProtocol.GRID_CELLS];

        // --- PER-BOT EPISODE STATE ---
        private Vector2[] botSpawnPos;
        private Vector2[] botTargetPos;
        private bool[] botHasZone;
        private bool[] hasQueuedZone;   // zone Python picked for this bot's next episode
        private Vector2[] queuedSpawnPos;
        private Vector2[] queuedTargetPos;
        private int[] botStepsInEpisode;
        private bool[] wasDead;
        private bool[] wasInactive;
        private bool[] pendingAbort;    // episode interrupted (training paused); end it on the next decision
        private int[] winCooldown;
        private byte[] flags;
        private int[] lastActions;

        // Path distance fields, one per target tile, built on first use (B2)
        private readonly Dictionary<Point, PathDistanceField> pathFields = new Dictionary<Point, PathDistanceField>();

        private int ticksUntilDecision = 0;
        private bool trainingActive = false;
        private bool everStarted = false; // Python has running episodes on this connection
        private int reconnectTimer = 0;

        public override void OnWorldLoad()
        {
            TrainingConfigLoader.LoadConfig(force: true);
            BATCH_SIZE = TrainingConfigLoader.NumBots;
            MAX_STEPS_PER_ZONE = TrainingConfigLoader.MaxStepsPerZone;
            FRAME_SKIP = TrainingConfigLoader.FrameSkip;
            DEBUG = TrainingConfigLoader.Debug;

            sendBuffer = new byte[RLProtocol.MaxPacketBytes(BATCH_SIZE)];
            terminalBuffer = new byte[BATCH_SIZE * RLProtocol.OBS_BYTES];

            botSpawnPos = new Vector2[BATCH_SIZE];
            botTargetPos = new Vector2[BATCH_SIZE];
            botHasZone = new bool[BATCH_SIZE];
            hasQueuedZone = new bool[BATCH_SIZE];
            queuedSpawnPos = new Vector2[BATCH_SIZE];
            queuedTargetPos = new Vector2[BATCH_SIZE];
            botStepsInEpisode = new int[BATCH_SIZE];
            wasDead = new bool[BATCH_SIZE];
            wasInactive = new bool[BATCH_SIZE];
            pendingAbort = new bool[BATCH_SIZE];
            winCooldown = new int[BATCH_SIZE];
            flags = new byte[BATCH_SIZE];
            lastActions = new int[BATCH_SIZE];
            pathFields.Clear();

            if (DEBUG) Main.NewText($"[RL] Allocated {sendBuffer.Length / 1024} KB network buffer for {BATCH_SIZE} bots", 100, 255, 100);

            connectFailureReported = false;
            if (AutomationArgs.RlEnabled) ConnectToPython();
        }

        public override void OnWorldUnload()
        {
            CleanupConnection();
            pathFields.Clear();
        }

        /// <summary>Forget all zones and episodes: a new connection is a new Python session.</summary>
        private void ResetEpisodeState() {
            for (int i = 0; i < BATCH_SIZE; i++) {
                botHasZone[i] = false;
                hasQueuedZone[i] = false;
                botSpawnPos[i] = Vector2.Zero;
                botTargetPos[i] = Vector2.Zero;
                botStepsInEpisode[i] = 0;
                wasDead[i] = false;
                wasInactive[i] = false;
                pendingAbort[i] = false;
                winCooldown[i] = 0;
                lastActions[i] = 0;
            }
            trainingActive = false;
            everStarted = false;
            ticksUntilDecision = 0;
        }

        // ---------------------------------------------------------------------------------
        // CONNECTION + HANDSHAKE (B9)
        // ---------------------------------------------------------------------------------

        private void ConnectToPython()
        {
            int port = TrainingConfigLoader.Port;
            try {
                if (!BitConverter.IsLittleEndian) throw new InvalidOperationException("big-endian CPU is not supported by the protocol");

                client = new TcpClient();
                client.NoDelay = true;
                client.Connect("127.0.0.1", port);
                stream = client.GetStream();
                writer = new BinaryWriter(stream);
                reader = new BinaryReader(stream);
            } catch {
                if (!connectFailureReported) {
                    Main.NewText($"Could not connect to AI Server on port {port} (retrying every {RECONNECT_INTERVAL / 60}s).", 255, 50, 50);
                    connectFailureReported = true;
                }
                CleanupConnection();
                return;
            }

            try {
                ResetEpisodeState();
                client.ReceiveTimeout = HANDSHAKE_TIMEOUT_MS;
                SendHandshake();
                if (!ReadHandshakeReply()) {
                    CleanupConnection();
                    return;
                }
                client.ReceiveTimeout = 0; // lock-step from here on: block as long as Python needs
                connectFailureReported = false;
                Main.NewText($"Connected to port {port}! Batch: {BATCH_SIZE}", 50, 255, 50);
            } catch (Exception e) {
                Main.NewText($"[RL] Handshake with Python failed: {e.Message}", 255, 50, 50);
                CleanupConnection();
            }
        }

        private void SendHandshake() {
            byte[] worldName = Encoding.UTF8.GetBytes(Main.worldName ?? "");
            writer.Write(RLProtocol.MAGIC);
            writer.Write(RLProtocol.PROTOCOL_VERSION);
            writer.Write(RLProtocol.OBS_VERSION);
            writer.Write(BATCH_SIZE);
            writer.Write(RLProtocol.STATS_SIZE);
            writer.Write(RLProtocol.GRID_SIDE);
            writer.Write(FRAME_SKIP);
            writer.Write(MAX_STEPS_PER_ZONE);
            writer.Write(worldName.Length);
            writer.Write(worldName);
            writer.Flush();
        }

        private bool ReadHandshakeReply() {
            int status = reader.ReadInt32();
            int messageLength = reader.ReadInt32();
            if (messageLength < 0 || messageLength > MAX_HANDSHAKE_MESSAGE_BYTES)
                throw new InvalidDataException($"bad handshake message length {messageLength}");
            string message = Encoding.UTF8.GetString(reader.ReadBytes(messageLength));

            if (status != RLProtocol.HANDSHAKE_OK) {
                Main.NewText($"[RL] Python rejected the connection: {message}", 255, 50, 50);
                if (TrainingConfigLoader.ConfigPath != null)
                    Main.NewText($"[RL] C# config: {TrainingConfigLoader.ConfigPath}", 255, 150, 150);
                return false;
            }
            if (message.Length > 0) Main.NewText($"[RL] {message}", 200, 200, 255);

            // Initial zone per bot; applied on the first decision tick, before the first packet.
            for (int i = 0; i < BATCH_SIZE; i++) {
                bool hasZone = reader.ReadInt32() != 0;
                Vector2 spawn = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                Vector2 target = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                if (hasZone) QueueZone(i, spawn, target);
            }
            return true;
        }

        private void CleanupConnection()
        {
            trainingActive = false;
            try { reader?.Dispose(); } catch { }
            try { writer?.Dispose(); } catch { }
            try { stream?.Dispose(); } catch { }
            try { client?.Close(); } catch { }
            client = null;
            stream = null;
            writer = null;
            reader = null;
        }

        // ---------------------------------------------------------------------------------
        // MAIN LOOP
        // ---------------------------------------------------------------------------------

        public override void PostUpdatePlayers()
        {
            if (!AutomationArgs.RlEnabled) return;

            // Auto-reconnect if not connected
            if (client == null) {
                reconnectTimer++;
                if (reconnectTimer >= RECONNECT_INTERVAL) {
                    reconnectTimer = 0;
                    ConnectToPython();
                }
                return;
            }

            if (BotManagerSystem.ActiveBots.Count < BATCH_SIZE)
            {
                if (trainingActive) {
                    Main.NewText($"Paused: Waiting for {BATCH_SIZE - BotManagerSystem.ActiveBots.Count} more bots.", 255, 255, 50);
                    trainingActive = false;
                }
                return;
            }

            if (!trainingActive) {
                Main.NewText($"Batch Full ({BATCH_SIZE})! Training Started.", 50, 255, 50);
                // On resume after a pause, the episodes Python is tracking were interrupted: end them.
                for (int i = 0; i < BATCH_SIZE; i++) {
                    pendingAbort[i] = everStarted;
                    lastActions[i] = 0;
                }
                everStarted = true;
                ticksUntilDecision = 0;
                trainingActive = true;
            }

            // Frame skip (B10): an action decided on tick D is used by the bot updates of ticks
            // D+1 .. D+FRAME_SKIP, and the next decision happens on tick D+FRAME_SKIP.
            if (ticksUntilDecision > 0) {
                ticksUntilDecision--;
                for (int i = 0; i < BATCH_SIZE; i++) {
                    int botIndex = GetBotIndex(i);
                    if (botIndex != -1) RLInterface.ApplyActionBits(botIndex, lastActions[i]);
                }
                return;
            }
            ticksUntilDecision = FRAME_SKIP - 1;

            try
            {
                DecisionTick();
            }
            catch (Exception e)
            {
                Main.NewText("AI Disconnected: " + e.Message, 255, 100, 100);
                CleanupConnection();
            }
        }

        private void DecisionTick()
        {
            bool hasMarker = TryFindTargetMarker(out Vector2 markerPos);

            // 1. EPISODE BOOKKEEPING (before the packet is built)
            int terminalBytes = 0;
            for (int i = 0; i < BATCH_SIZE; i++)
            {
                byte f = 0;
                int botIndex = GetBotIndex(i);
                Player bot = (botIndex != -1) ? Main.player[botIndex] : null;

                if (bot == null) {
                    // Bot despawned: end its episode once, then stay silent until it is back (B11)
                    if (!wasInactive[i]) {
                        f = RLProtocol.FLAG_DONE | RLProtocol.FLAG_ABORTED;
                        terminalBytes = WriteObservation(terminalBuffer, terminalBytes, -1, false, Vector2.Zero);
                        wasInactive[i] = true;
                        botHasZone[i] = false; // gets its queued zone when it comes back
                        wasDead[i] = false;
                    }
                    pendingAbort[i] = false;
                    flags[i] = f;
                    continue;
                }
                wasInactive[i] = false;

                bool hasTarget = TryGetTarget(i, hasMarker, markerPos, out Vector2 target);
                if (pendingAbort[i]) {
                    f = RLProtocol.FLAG_DONE | RLProtocol.FLAG_ABORTED;
                    pendingAbort[i] = false;
                } else if (hasTarget) {
                    f = EvaluateEpisodeEnd(i, bot, target);
                }

                if ((f & RLProtocol.FLAG_DONE) != 0) {
                    // Terminal observation of the old episode, then start the next one right away (B8)
                    terminalBytes = WriteObservation(terminalBuffer, terminalBytes, botIndex, hasTarget, target);
                    if (hasQueuedZone[i]) {
                        StartQueuedZone(i, bot);
                        f |= RLProtocol.FLAG_ZONE_SWITCHED;
                    } else if (botHasZone[i]) {
                        StartZone(i, bot, botSpawnPos[i], botTargetPos[i]); // no new zone: replay this one
                    } else {
                        botStepsInEpisode[i] = 0;
                    }
                } else if (!botHasZone[i] && hasQueuedZone[i]) {
                    // First episode (or bot came back after despawning)
                    StartQueuedZone(i, bot);
                    f |= RLProtocol.FLAG_ZONE_SWITCHED;
                }
                flags[i] = f;
            }

            // 2. OBSERVATIONS of the current state (after any teleport above)
            int offset = 0;
            for (int i = 0; i < BATCH_SIZE; i++) {
                int botIndex = GetBotIndex(i);
                Vector2 target = Vector2.Zero;
                bool hasTarget = botIndex != -1 && TryGetTarget(i, hasMarker, markerPos, out target);
                float[] stats = (botIndex != -1) ? RLInterface.GetObservation(botIndex, hasTarget, target) : emptyStats;
                Buffer.BlockCopy(stats, 0, sendBuffer, offset, RLProtocol.STATS_BYTES);
                offset += RLProtocol.STATS_BYTES;
            }
            for (int i = 0; i < BATCH_SIZE; i++) {
                int botIndex = GetBotIndex(i);
                float[] grid = (botIndex != -1) ? GridSensors.GetObservationGrid(Main.player[botIndex]) : emptyGrid;
                Buffer.BlockCopy(grid, 0, sendBuffer, offset, RLProtocol.GRID_BYTES);
                offset += RLProtocol.GRID_BYTES;
            }

            // 3. PATH DISTANCE + FLAGS
            for (int i = 0; i < BATCH_SIZE; i++) {
                int botIndex = GetBotIndex(i);
                float dist = RLProtocol.NO_DISTANCE;
                if (botIndex != -1 && TryGetTarget(i, hasMarker, markerPos, out Vector2 target)) {
                    dist = PathDistanceTiles(Main.player[botIndex], target);
                }
                BinaryPrimitives.WriteSingleLittleEndian(sendBuffer.AsSpan(offset, sizeof(float)), dist);
                offset += sizeof(float);
                sendBuffer[offset++] = flags[i];

                if (DEBUG && i == 0 && botHasZone[0] && botStepsInEpisode[0] % DEBUG_LOG_INTERVAL == 0 && botStepsInEpisode[0] > 0) {
                    Main.NewText($"[RL] Bot 0 steps {botStepsInEpisode[0]}/{MAX_STEPS_PER_ZONE}, path dist {dist:F1}, target {botTargetPos[0].X:F0},{botTargetPos[0].Y:F0}", 200, 200, 255);
                }
            }

            // 4. TERMINAL OBSERVATIONS of bots whose episode ended this tick
            Buffer.BlockCopy(terminalBuffer, 0, sendBuffer, offset, terminalBytes);
            offset += terminalBytes;

            stream.Write(sendBuffer, 0, offset);

            // 5. READ ACTIONS (+ next-zone assignments) and apply them
            for (int i = 0; i < BATCH_SIZE; i++)
            {
                int command = reader.ReadInt32();
                if (command >= RLProtocol.ZONE_COMMAND_OFFSET)
                {
                    command -= RLProtocol.ZONE_COMMAND_OFFSET;
                    Vector2 spawn = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    Vector2 target = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    QueueZone(i, spawn, target);
                }
                lastActions[i] = command & RLProtocol.ACTION_MASK;

                int botIndex = GetBotIndex(i);
                if (botIndex != -1) RLInterface.ApplyActionBits(botIndex, lastActions[i]);
            }
        }

        /// <summary>Win / death / timeout check for one bot with a target. Outcomes are exclusive.</summary>
        private byte EvaluateEpisodeEnd(int i, Player bot, Vector2 target)
        {
            if (winCooldown[i] > 0) winCooldown[i]--;
            if (botHasZone[i]) botStepsInEpisode[i]++;

            if (bot.dead) {
                if (wasDead[i]) return 0; // already reported this death
                wasDead[i] = true;
                if (DEBUG) Main.NewText($"[RL] Bot {i} DIED!", 255, 50, 50);
                return RLProtocol.FLAG_DONE | RLProtocol.FLAG_DIE;
            }
            wasDead[i] = false;

            float distTiles = Vector2.Distance(bot.Center, target) / 16f;
            if (distTiles < WIN_RADIUS_TILES && winCooldown[i] == 0) {
                winCooldown[i] = WIN_COOLDOWN_STEPS;
                if (DEBUG) Main.NewText($"[RL] Bot {i} WIN! Dist={distTiles:F1}", 50, 255, 50);
                return RLProtocol.FLAG_DONE | RLProtocol.FLAG_WIN;
            }

            if (botHasZone[i] && botStepsInEpisode[i] >= MAX_STEPS_PER_ZONE) {
                if (DEBUG) Main.NewText($"[RL] Bot {i} TIMEOUT!", 255, 200, 100);
                return RLProtocol.FLAG_DONE | RLProtocol.FLAG_TIMEOUT;
            }
            return 0;
        }

        // ---------------------------------------------------------------------------------
        // ZONES
        // ---------------------------------------------------------------------------------

        private void QueueZone(int i, Vector2 spawnPos, Vector2 targetPos) {
            hasQueuedZone[i] = true;
            queuedSpawnPos[i] = spawnPos;
            queuedTargetPos[i] = targetPos;
        }

        private void StartQueuedZone(int i, Player bot) {
            hasQueuedZone[i] = false;
            StartZone(i, bot, queuedSpawnPos[i], queuedTargetPos[i]);
        }

        /// <summary>Start a new episode for bot i in the given zone and teleport it to the spawn point.</summary>
        private void StartZone(int i, Player bot, Vector2 spawnPos, Vector2 targetPos)
        {
            botSpawnPos[i] = spawnPos;
            botTargetPos[i] = targetPos;
            botHasZone[i] = true;
            botStepsInEpisode[i] = 0;
            winCooldown[i] = 0;

            // The spawn point is where the bot's feet go. Player.Teleport takes the top-left corner,
            // so convert once (the old code set the feet position and then called Teleport with the
            // raw point, which put the top-left corner there instead, 42px lower).
            Vector2 topLeft = spawnPos - new Vector2(bot.width / 2f, bot.height);
            bot.Teleport(topLeft, 0);
            bot.velocity = Vector2.Zero;
            bot.fallStart = bot.fallStart2 = (int)(bot.position.Y / 16f);

            if (DEBUG) Main.NewText($"[RL] Bot {i} TP -> ({spawnPos.X:F0}, {spawnPos.Y:F0})", 100, 255, 100);
        }

        private bool TryGetTarget(int i, bool hasMarker, Vector2 markerPos, out Vector2 target) {
            if (botHasZone[i]) { target = botTargetPos[i]; return true; }
            if (hasMarker) { target = markerPos; return true; }
            target = Vector2.Zero;
            return false;
        }

        /// <summary>One scan over projectiles per decision tick (not per bot).</summary>
        private static bool TryFindTargetMarker(out Vector2 pos) {
            int targetType = ModContent.ProjectileType<TargetMarkerProjectile>();
            for (int i = 0; i < Main.maxProjectiles; i++) {
                if (Main.projectile[i].active && Main.projectile[i].type == targetType) {
                    pos = Main.projectile[i].Center;
                    return true;
                }
            }
            pos = Vector2.Zero;
            return false;
        }

        // ---------------------------------------------------------------------------------
        // PATH DISTANCE (B2/B3)
        // ---------------------------------------------------------------------------------

        /// <summary>
        /// Walking/jumping distance in tiles from the bot to the target, from a BFS field that is
        /// built once per target and cached. Outside the field or when the bot's cell is not
        /// connected to the target, falls back to straight-line distance + a penalty.
        /// </summary>
        private float PathDistanceTiles(Player bot, Vector2 target)
        {
            PathDistanceField field = GetPathField(target);
            int tileX = (int)(bot.Center.X / 16f);
            int feetTileY = (int)((bot.Bottom.Y - 1f) / 16f);
            int d = field.GetDistance(tileX, feetTileY);
            if (d == PathDistanceField.UNREACHABLE) d = field.GetDistance(tileX, feetTileY - 1);
            if (d != PathDistanceField.UNREACHABLE) return d;
            return Vector2.Distance(bot.Center, target) / 16f + TrainingConfigLoader.OffPathPenaltyTiles;
        }

        private PathDistanceField GetPathField(Vector2 target)
        {
            Point key = new Point((int)(target.X / 16f), (int)(target.Y / 16f));
            if (!pathFields.TryGetValue(key, out PathDistanceField field)) {
                if (pathFields.Count >= MAX_CACHED_PATH_FIELDS) pathFields.Clear(); // e.g. marker moved many times
                field = PathDistanceField.Build(GridSensors.ClassifyPathTile, target.X / 16f, target.Y / 16f,
                    TrainingConfigLoader.PathFieldRadius, TrainingConfigLoader.MaxJumpTiles, WIN_RADIUS_TILES);
                pathFields[key] = field;
            }
            return field;
        }

        // ---------------------------------------------------------------------------------
        // HELPERS
        // ---------------------------------------------------------------------------------

        /// <summary>Copy stats + grid of one bot (or zeros for botIndex -1) into a buffer.</summary>
        private int WriteObservation(byte[] buffer, int offset, int botIndex, bool hasTarget, Vector2 target)
        {
            // GetObservation / GetObservationGrid return shared arrays: copy before the next call.
            float[] stats = (botIndex != -1) ? RLInterface.GetObservation(botIndex, hasTarget, target) : emptyStats;
            Buffer.BlockCopy(stats, 0, buffer, offset, RLProtocol.STATS_BYTES);
            offset += RLProtocol.STATS_BYTES;
            float[] grid = (botIndex != -1) ? GridSensors.GetObservationGrid(Main.player[botIndex]) : emptyGrid;
            Buffer.BlockCopy(grid, 0, buffer, offset, RLProtocol.GRID_BYTES);
            return offset + RLProtocol.GRID_BYTES;
        }

        private int GetBotIndex(int i) {
            if (i < BotManagerSystem.ActiveBots.Count) {
                int idx = BotManagerSystem.ActiveBots[i];
                if (idx >= 0 && idx < Main.maxPlayers && Main.player[idx].active) {
                    return idx;
                }
            }
            return -1;
        }
    }
}
