using System;
using System.Net.Sockets;
using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Microsoft.Xna.Framework;
using System.Collections.Generic;
using FirstMod_Sp.Content.Projectiles;

namespace FirstMod_Sp.Common.Systems
{
    public class RLNetworkSystem : ModSystem
    {
        private TcpClient client;
        private NetworkStream stream;
        private BinaryWriter writer;
        private BinaryReader reader;

        // CONFIG VALUES (loaded from training_config.json)
        private int BATCH_SIZE; 
        private int MAX_STEPS_PER_ZONE;
        private int FRAME_SKIP;
        
        // DYNAMIC ARRAYS (sized based on config)
        private byte[] sendBuffer;
        private float[] emptyStats = new float[46]; 
        private float[] emptyGrid = new float[1600];
        
        // --- PER-BOT ZONE TARGETS ---
        private Vector2[] botSpawnPos;
        private Vector2[] botTargetPos;
        private bool[] botHasZone;
        
        // --- ZONE TIMEOUT TRACKING ---
        private int[] botStepsSinceZoneAssignment;
        
        // --- FLOW FIELD VARIABLES ---
        private int[,] flowMap; 
        private Point flowMapCenter;
        private const int FLOW_MAP_SIZE = 120;
        private Vector2 lastTargetPosForFlow = Vector2.Zero;
        // ----------------------------

        private float[] prevMetric;
        private bool[] wasDead;
        private int[] justTeleportedTimer; // Timer to suppress rewards after teleport
        private int[] winCooldown; // Debounce timer for victory
        
        // FRAME SKIPPING
        private int framesSinceUpdate = 0;
        private int[] lastActions;

        private bool trainingActive = false;
        private int reconnectTimer = 0;
        private const int RECONNECT_INTERVAL = 120; // ~2 seconds at 60fps

        public override void OnWorldLoad() 
        { 
            // Load configuration first
            TrainingConfigLoader.LoadConfig();
            BATCH_SIZE = TrainingConfigLoader.NumBots;
            MAX_STEPS_PER_ZONE = TrainingConfigLoader.MaxStepsPerZone;
            FRAME_SKIP = TrainingConfigLoader.FrameSkip;
            
            // Allocate arrays based on config
            // Each bot sends ~1650 floats (vision+stats) = 6600 bytes
            // Add safety margin + Protocol Overhead
            int bufferSize = (BATCH_SIZE * 7000) + 4096; 
            sendBuffer = new byte[bufferSize]; 
            
            Main.NewText($"[RL] Allocated {bufferSize/1024} KB network buffer for {BATCH_SIZE} bots", 100, 255, 100);
            
            botSpawnPos = new Vector2[BATCH_SIZE];
            botTargetPos = new Vector2[BATCH_SIZE];
            botHasZone = new bool[BATCH_SIZE];
            botStepsSinceZoneAssignment = new int[BATCH_SIZE];
            prevMetric = new float[BATCH_SIZE];
            wasDead = new bool[BATCH_SIZE];
            justTeleportedTimer = new int[BATCH_SIZE];
            winCooldown = new int[BATCH_SIZE];
            lastActions = new int[BATCH_SIZE];
            flowMap = new int[FLOW_MAP_SIZE, FLOW_MAP_SIZE];
            
            ConnectToPython(); 
            ResetTrackers();
        }

        public override void OnWorldUnload() 
        { 
            CleanupConnection(); 
        }

        private void ResetTrackers() {
            for(int i=0; i<BATCH_SIZE; i++) {
                prevMetric[i] = -1f; 
                wasDead[i] = false;
                lastActions[i] = 0;
                botHasZone[i] = false;
                botSpawnPos[i] = Vector2.Zero;
                botTargetPos[i] = Vector2.Zero;
                botStepsSinceZoneAssignment[i] = 0;
            }
            trainingActive = false;
            framesSinceUpdate = 0;
        }

        /// <summary>
        /// Assign a zone to a specific bot and teleport them to the spawn point
        /// </summary>
        public void AssignZoneToBot(int batchIndex, Vector2 spawnPos, Vector2 targetPos)
        {
            if (batchIndex < 0 || batchIndex >= BATCH_SIZE) return;
            
            botSpawnPos[batchIndex] = spawnPos;
            botTargetPos[batchIndex] = targetPos;
            botHasZone[batchIndex] = true;
            prevMetric[batchIndex] = -1f;  // Reset progress tracking
            botStepsSinceZoneAssignment[batchIndex] = 0;  // Reset step counter for new zone
            justTeleportedTimer[batchIndex] = 20; // Suppress checks (increased to 20)
            winCooldown[batchIndex] = 0; // Reset cooldown
            
            // Teleport the bot to spawn position
            // Teleport the bot to spawn position
            int botIndex = GetBotIndex(batchIndex);
            if (botIndex != -1 && Main.player[botIndex].active)
            {
                Player bot = Main.player[botIndex];
                
                // FORCE POSITION instead of asking nicely with Teleport()
                // This prevents "failed to teleport because block" issues
                bot.position = spawnPos - new Vector2(bot.width/2, bot.height);
                bot.velocity = Vector2.Zero;
                
                // Also trigger internal teleport method to reset state but with style 0 (silent)
                bot.Teleport(spawnPos, 0); 
                
                Main.NewText($"[RL] Bot {batchIndex} FORCED TP -> ({spawnPos.X:F0}, {spawnPos.Y:F0})", 100, 255, 100);
            }
            else
            {
                Main.NewText($"[RL] WARNING: Bot {batchIndex} not active yet", 255, 200, 100);
            }
        }

        /// <summary>
        /// Get the target position for a specific bot
        /// </summary>
        public Vector2 GetBotTarget(int batchIndex)
        {
            if (batchIndex >= 0 && batchIndex < BATCH_SIZE && botHasZone[batchIndex])
                return botTargetPos[batchIndex];
            return Vector2.Zero;
        }

        private void ConnectToPython()
        {
            try {
                int port = TrainingConfigLoader.Port;
                client = new TcpClient("127.0.0.1", port); 
                client.NoDelay = true; 
                stream = client.GetStream();
                writer = new BinaryWriter(stream);
                reader = new BinaryReader(stream);
                Main.NewText($"Connected to port {port}! Batch: {BATCH_SIZE}", 50, 255, 50);
            } catch {
                Main.NewText("Could not connect to AI Server.", 255, 50, 50);
                CleanupConnection();
            }
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

        // --- THE SCENT LOGIC (BFS) ---
        private void UpdateFlowField(Vector2 targetPos) {
            // Only update if target moved more than 1 block (optimization)
            if (Vector2.Distance(targetPos, lastTargetPosForFlow) < 16f) return;
            
            lastTargetPosForFlow = targetPos;
            Point tCenter = targetPos.ToTileCoordinates();
            flowMapCenter = tCenter;

            // 1. Reset Map to -1 (Unreachable)
            for(int x=0; x<FLOW_MAP_SIZE; x++)
                for(int y=0; y<FLOW_MAP_SIZE; y++)
                    flowMap[x, y] = -1;

            // 2. BFS Setup
            Queue<Point> queue = new Queue<Point>();
            
            // Local coordinates relative to the array (Center is FLOW_MAP_SIZE/2)
            int halfSize = FLOW_MAP_SIZE / 2;
            Point startLocal = new Point(halfSize, halfSize);
            
            flowMap[startLocal.X, startLocal.Y] = 0;
            queue.Enqueue(startLocal);

            // 3. Flood Fill
            while(queue.Count > 0) {
                Point p = queue.Dequeue();
                int currentDist = flowMap[p.X, p.Y];

                // Stop if path is too long (optimization)
                if (currentDist > 200) continue;

                // Check Neighbors
                Point[] neighbors = { 
                    new Point(p.X+1, p.Y), new Point(p.X-1, p.Y), 
                    new Point(p.X, p.Y+1), new Point(p.X, p.Y-1) 
                };

                foreach(Point n in neighbors) {
                    // Bounds Check (Local Array)
                    if (n.X < 0 || n.X >= FLOW_MAP_SIZE || n.Y < 0 || n.Y >= FLOW_MAP_SIZE) continue;
                    
                    // If already visited, skip
                    if (flowMap[n.X, n.Y] != -1) continue;

                    // World Coordinates conversion
                    int worldX = flowMapCenter.X + (n.X - halfSize);
                    int worldY = flowMapCenter.Y + (n.Y - halfSize);

                    if (!WorldGen.InWorld(worldX, worldY)) continue;

                    // Collision Check
                    Tile t = Main.tile[worldX, worldY];
                    bool isSolid = t.HasUnactuatedTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType];
                    
                    if (!isSolid) {
                        flowMap[n.X, n.Y] = currentDist + 1;
                        queue.Enqueue(n);
                    }
                }
            }
        }

        private float GetFlowDistance(Vector2 botPos) {
            Point tPos = botPos.ToTileCoordinates();
            int halfSize = FLOW_MAP_SIZE / 2;
            
            // Convert world pos to local map pos
            int localX = tPos.X - flowMapCenter.X + halfSize;
            int localY = tPos.Y - flowMapCenter.Y + halfSize;

            // Check bounds
            if (localX < 0 || localX >= FLOW_MAP_SIZE || localY < 0 || localY >= FLOW_MAP_SIZE) {
                // Fallback to Euclidean if outside the scent range
                return Vector2.Distance(botPos, lastTargetPosForFlow) / 16f;
            }

            int dist = flowMap[localX, localY];
            
            // If unreachable (walled off) or not computed, fallback to Euclidean * 2 (Penalty for being off-path)
            if (dist == -1) return (Vector2.Distance(botPos, lastTargetPosForFlow) / 16f) + 50f;

            return (float)dist;
        }
        // -----------------------------

        public override void PostUpdatePlayers()
        {
            // Auto-reconnect if not connected
            if (client == null || !client.Connected) {
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
                ResetTrackers();
                trainingActive = true;
            }

            // Frame Skipping
            if (framesSinceUpdate < FRAME_SKIP) {
                for (int i = 0; i < BATCH_SIZE; i++) {
                    int botIndex = GetBotIndex(i);
                    if (botIndex != -1) {
                        float[] actionParams = ConvertActionToParams(lastActions[i]);
                        RLInterface.ApplyAction(botIndex, actionParams);
                    }
                }
                framesSinceUpdate++;
                return;
            }
            framesSinceUpdate = 0; 

            try 
            {
                int bufferOffset = 0;

                // 0. FIND TARGET & UPDATE FLOW FIELD
                Vector2 targetPos = Vector2.Zero;
                bool hasTarget = false;
                int targetType = ModContent.ProjectileType<TargetMarkerProjectile>();
                for (int i = 0; i < Main.maxProjectiles; i++) {
                    if (Main.projectile[i].active && Main.projectile[i].type == targetType) {
                        targetPos = Main.projectile[i].Center;
                        hasTarget = true;
                        break;
                    }
                }

                // Run the BFS if target exists
                if (hasTarget) UpdateFlowField(targetPos);

                // 1. SEND STATS
                for (int i = 0; i < BATCH_SIZE; i++)
                {
                    int botIndex = GetBotIndex(i);
                    float[] stats = (botIndex != -1) ? RLInterface.GetObservation(botIndex) : emptyStats;
                    if (botIndex != -1) SanitizeArray(stats);
                    Buffer.BlockCopy(stats, 0, sendBuffer, bufferOffset, 184); 
                    bufferOffset += 184;
                }

                // 2. SEND GRIDS
                for (int i = 0; i < BATCH_SIZE; i++)
                {
                    int botIndex = GetBotIndex(i);
                    Player bot = (botIndex != -1) ? Main.player[botIndex] : null;
                    float[] grid = (bot != null) ? GridSensors.GetObservationGrid(bot) : emptyGrid;
                    if (bot != null) SanitizeArray(grid);
                    Buffer.BlockCopy(grid, 0, sendBuffer, bufferOffset, 6400); 
                    bufferOffset += 6400;
                }

                for (int i = 0; i < BATCH_SIZE; i++)
                {
                    int botIndex = GetBotIndex(i);
                    Player bot = (botIndex != -1) ? Main.player[botIndex] : null;
                    
                    // ------------------------------------------------------------------------
                    // REWARD REFACTOR: SEND RAW DATA + FLAGS
                    // ------------------------------------------------------------------------
                    
                    float distRaw = -1.0f;
                    byte flags = 0; // Bit 0=Done, 1=Win, 2=Die, 3=Timeout

                    // GRACE PERIOD: If timer > 0, decrement and skip logic
                    if (justTeleportedTimer[i] > 0) {
                        justTeleportedTimer[i]--;
                        distRaw = -1f; // Neutral
                        // Flags = 0 (Not Done)
                    }
                    else if (bot != null && bot.active) {
                        
                        if (winCooldown[i] > 0) winCooldown[i]--; // Decrement cooldown
                        
                        // Determine Target Logic
                        Vector2 thisTargetPos = Vector2.Zero;
                        bool thisHasTarget = false;
                        
                        if (botHasZone[i]) {
                            thisTargetPos = botTargetPos[i];
                            thisHasTarget = true;
                        } else if (hasTarget) {
                            thisTargetPos = targetPos;
                            thisHasTarget = true;
                        }
                        
                        // Calculate Raw Distance (in blocks)
                        if (thisHasTarget) {
                            distRaw = Vector2.Distance(bot.Center, thisTargetPos) / 16f;
                        }

                        // CHECK FLAGS (Done/Win/Die/Timeout)
                        bool isDone = false;
                        bool isWin = false;
                        bool isDie = false;
                        bool isTimeout = false;

                        // 1. WIN
                        if (thisHasTarget && distRaw < 2.0f && winCooldown[i] == 0) { // < 32 pixels
                            isWin = true;
                            isDone = true;
                            winCooldown[i] = 100; // Debounce
                            Main.NewText($"[RL] Bot {i} WIN! Dist={distRaw:F1}", 50, 255, 50);
                        }

                        // 2. DIE
                        if (bot.dead) {
                            if (!wasDead[i]) {
                                isDie = true;
                                isDone = true;
                                winCooldown[i] = 100; // Debounce
                                Main.NewText($"[RL] Bot {i} DIED!", 255, 50, 50);
                            }
                            wasDead[i] = true;
                        } else {
                            wasDead[i] = false;
                        }
                        
                        // 3. TIMEOUT
                        if (botHasZone[i]) {
                            botStepsSinceZoneAssignment[i]++;
                            if (botStepsSinceZoneAssignment[i] >= MAX_STEPS_PER_ZONE && winCooldown[i] == 0) {
                                isTimeout = true;
                                isDone = true;
                                winCooldown[i] = 100; // Debounce
                                Main.NewText($"[RL] Bot {i} TIMEOUT!", 255, 200, 100);
                            }
                            // DEBUG: Log progress for Bot 0
                            if (i == 0 && botStepsSinceZoneAssignment[i] % 100 == 0) {
                                Main.NewText($"[RL] Bot 0 Steps: {botStepsSinceZoneAssignment[i]}/{MAX_STEPS_PER_ZONE}", 200, 200, 255);
                            }
                        }

                        // Pack Flags
                        if (isDone) flags |= 1;
                        if (isWin)  flags |= 2;
                        if (isDie)  flags |= 4;
                        if (isTimeout) flags |= 8;
                    } 
                    else {
                        // BOT IS INACTIVE (Despawned?)
                        // Reset persistent state so next bot works correctly
                        botHasZone[i] = false;
                        winCooldown[i] = 0;
                        botStepsSinceZoneAssignment[i] = 0;
                        botTargetPos[i] = Vector2.Zero;
                        wasDead[i] = false;
                        
                        // Send "Done" flag to ensure Python resets its tracker too
                        flags |= 1; 
                        distRaw = -1f;
                    }
                    
                    if (bot != null && bot.active) wasDead[i] = false;

                    // WRITE PACKET
                    // Slot 1: Raw Distance (Float) - replaces 'reward'
                    byte[] distBytes = BitConverter.GetBytes(distRaw);
                    sendBuffer[bufferOffset] = distBytes[0];
                    sendBuffer[bufferOffset+1] = distBytes[1];
                    sendBuffer[bufferOffset+2] = distBytes[2];
                    sendBuffer[bufferOffset+3] = distBytes[3];
                    bufferOffset += 4;

                    // Slot 2: Flags (Byte) - replaces 'isDone' byte
                    sendBuffer[bufferOffset] = flags;
                    bufferOffset += 1;
                }

                stream.Write(sendBuffer, 0, bufferOffset);

                // 4. READ & EXECUTE ACTIONS + ZONE ASSIGNMENTS
                for (int i = 0; i < BATCH_SIZE; i++)
                {
                    int command = reader.ReadInt32();
                    
                    // DEBUG: Log first bot command to check protocol
                    if (i == 0 && command >= 1000000) {
                        Main.NewText($"[RL] RX Bot 0 CMD: {command}", 255, 255, 0);
                    }

                    // Check if this is a zone assignment command (command >= 1000000)
                    if (command >= 1000000)
                    {
                        // Extract actual action
                        int actionIndex = command - 1000000;
                        lastActions[i] = actionIndex;
                        
                        // Read zone coordinates (4 floats = 16 bytes)
                        float spawnX = reader.ReadSingle();
                        float spawnY = reader.ReadSingle();
                        float targetX = reader.ReadSingle();
                        float targetY = reader.ReadSingle();
                        
                        // Assign zone to bot and teleport
                        Vector2 spawnPos = new Vector2(spawnX, spawnY);
                        Vector2 zoneTargetPos = new Vector2(targetX, targetY);
                        AssignZoneToBot(i, spawnPos, zoneTargetPos);
                        
                        // Apply action
                        int botIndex = GetBotIndex(i);
                        if (botIndex != -1) {
                            float[] actionParams = ConvertActionToParams(actionIndex);
                            RLInterface.ApplyAction(botIndex, actionParams);
                        }
                    }
                    else
                    {
                        // Normal action
                        lastActions[i] = command;
                        
                        int botIndex = GetBotIndex(i);
                        if (botIndex != -1) {
                            float[] actionParams = ConvertActionToParams(command);
                            RLInterface.ApplyAction(botIndex, actionParams);
                        }
                    }
                }
            }
            catch (Exception e) 
            {
                Main.NewText("AI Disconnected: " + e.Message, 255, 100, 100);
                CleanupConnection();
            }
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

        private void SanitizeArray(float[] data)
        {
            for(int i=0; i<data.Length; i++) {
                if (float.IsNaN(data[i]) || float.IsInfinity(data[i])) data[i] = 0f;
            }
        }

        private float[] ConvertActionToParams(int actionFlags)
        {
            float[] p = new float[8]; 
            if ((actionFlags & 1) != 0) p[0] = 1f; // Left
            if ((actionFlags & 2) != 0) p[1] = 1f; // Right
            if ((actionFlags & 4) != 0) p[2] = 1f; // Jump
            return p;
        }
    }
}