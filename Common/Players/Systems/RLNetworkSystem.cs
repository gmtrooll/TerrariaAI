using System;
using System.Net.Sockets;
using System.IO;
using Terraria;
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

        private const int BATCH_SIZE = 16; 
        private byte[] sendBuffer = new byte[262144]; 
        private float[] emptyStats = new float[46]; 
        private float[] emptyGrid = new float[1600];
        
        // --- FLOW FIELD VARIABLES ---
        // We calculate "True Path Distance" instead of "Air Distance"
        private int[,] flowMap; 
        private Point flowMapCenter;
        private const int FLOW_MAP_SIZE = 120; // 120x120 tiles (covers ~screen size)
        private Vector2 lastTargetPosForFlow = Vector2.Zero;
        // ----------------------------

        private float[] prevMetric = new float[BATCH_SIZE]; 
        private bool[] wasDead = new bool[BATCH_SIZE];
        
        // FRAME SKIPPING
        private const int FRAME_SKIP = 4; 
        private int framesSinceUpdate = 0;
        private int[] lastActions = new int[BATCH_SIZE];

        private bool trainingActive = false;

        public override void OnWorldLoad() 
        { 
            ConnectToPython(); 
            ResetTrackers();
            // Init Flow Map memory
            flowMap = new int[FLOW_MAP_SIZE, FLOW_MAP_SIZE];
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
            }
            trainingActive = false;
            framesSinceUpdate = 0;
        }

        private void ConnectToPython()
        {
            try {
                client = new TcpClient("127.0.0.1", 5000); 
                client.NoDelay = true; 
                stream = client.GetStream();
                writer = new BinaryWriter(stream);
                reader = new BinaryReader(stream);
                Main.NewText($"Connected! Batch: {BATCH_SIZE}", 50, 255, 50);
            } catch {
                Main.NewText("Could not connect to AI Server.", 255, 50, 50);
                CleanupConnection();
            }
        }

        private void CleanupConnection()
        {
            client?.Close();
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
            if (client == null || !client.Connected) return;

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

                // 3. SEND REWARDS (FLOW FIELD LOGIC)
                for (int i = 0; i < BATCH_SIZE; i++)
                {
                    int botIndex = GetBotIndex(i);
                    Player bot = (botIndex != -1) ? Main.player[botIndex] : null;
                    
                    float reward = 0f;
                    bool isDone = false;

                    if (bot != null && bot.active) {
                        
                        // Calculate "Metric" (Distance)
                        float currentMetric = 0f;

                        if (hasTarget) {
                            // Use FLOW DISTANCE (Scent) instead of Air Distance
                            currentMetric = GetFlowDistance(bot.Center);
                        } else {
                            // Fallback: Run Right (Negative X is bad, Positive X is good)
                            // We treat "Metric" as "Distance from Goal", so we invert X.
                            // Moving Right -> Metric Decreases.
                            currentMetric = -bot.position.X / 16f; 
                        }

                        if (prevMetric[i] == -1f) prevMetric[i] = currentMetric;

                        // IMPROVEMENT = OldMetric - NewMetric
                        // If FlowDistance goes 50 -> 49, Improvement is +1. Good.
                        float progress = prevMetric[i] - currentMetric;
                        
                        // Strong Reward for following the scent
                        reward += progress * 0.5f; 
                        
                        prevMetric[i] = currentMetric;

                        // Target reached
                        if (hasTarget) {
                            // Simple Euclidean check for final touch
                            if (Vector2.Distance(bot.Center, targetPos) < 32f) { 
                                reward += 20.0f; 
                                isDone = true;   
                                prevMetric[i] = -1f; 
                            }
                        }

                        if (bot.dead) {
                            if (!wasDead[i]) {
                                reward -= 5.0f;
                                isDone = true;
                            }
                            wasDead[i] = true;
                            prevMetric[i] = -1f;
                        } else {
                            wasDead[i] = false;
                        }
                    } else {
                        prevMetric[i] = -1f;
                        wasDead[i] = false;
                        isDone = true; 
                    }
                    
                    if (bot != null && bot.active) wasDead[i] = false;

                    byte[] rewardBytes = BitConverter.GetBytes(reward);
                    sendBuffer[bufferOffset] = rewardBytes[0];
                    sendBuffer[bufferOffset+1] = rewardBytes[1];
                    sendBuffer[bufferOffset+2] = rewardBytes[2];
                    sendBuffer[bufferOffset+3] = rewardBytes[3];
                    bufferOffset += 4;

                    sendBuffer[bufferOffset] = isDone ? (byte)1 : (byte)0;
                    bufferOffset += 1;
                }

                stream.Write(sendBuffer, 0, bufferOffset);

                // 4. READ & EXECUTE
                for (int i = 0; i < BATCH_SIZE; i++)
                {
                    int actionIndex = reader.ReadInt32();
                    lastActions[i] = actionIndex;

                    int botIndex = GetBotIndex(i);
                    if (botIndex != -1) {
                        float[] actionParams = ConvertActionToParams(actionIndex);
                        RLInterface.ApplyAction(botIndex, actionParams);
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