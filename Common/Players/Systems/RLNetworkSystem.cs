using System;
using System.Net.Sockets;
using System.IO;
using Terraria;
using Terraria.ModLoader;
using Microsoft.Xna.Framework;
using System.Collections.Generic;

namespace FirstMod_Sp.Common.Systems
{
    public class RLNetworkSystem : ModSystem
    {
        private TcpClient client;
        private NetworkStream stream;
        private BinaryWriter writer;
        private BinaryReader reader;

        // Reward Tracking
        private float lastXPosition;
        private bool wasDead;
        private int currentBotTarget = -1; // Track which bot we are currently training
        
        public override void OnWorldLoad()
        {
            ConnectToPython();
        }

        public override void OnWorldUnload()
        {
            CleanupConnection();
        }

        private void ConnectToPython()
        {
            try {
                client = new TcpClient("127.0.0.1", 5000); 
                client.NoDelay = true;
                stream = client.GetStream();
                writer = new BinaryWriter(stream);
                reader = new BinaryReader(stream);
                Main.NewText("Connected to AI Server!", 50, 255, 50);
            } catch {
                Main.NewText("Could not connect to AI Server. Is Python running?", 255, 50, 50);
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

        public override void PostUpdatePlayers()
        {
            if (client == null || !client.Connected) return;

            // 1. FIND A BOT TO TRAIN
            // We look for the first available bot in the list
            int targetIndex = -1;
            if (BotManagerSystem.ActiveBots.Count > 0) {
                targetIndex = BotManagerSystem.ActiveBots[0];
            }

            // If no bots are alive/spawned, we wait.
            // Python will hang waiting for data, but that is fine until you spawn a bot.
            if (targetIndex == -1) return;

            Player bot = Main.player[targetIndex];

            // If we switched targets (or just started), reset tracking variables
            if (currentBotTarget != targetIndex) {
                currentBotTarget = targetIndex;
                lastXPosition = bot.position.X;
                wasDead = false;
            }

            try 
            {
                // --- 2. CALCULATE REWARD ---
                float reward = 0f;
                bool isDone = false;

                // Simple Reward: Run Right
                float progress = (bot.position.X - lastXPosition);
                reward += progress * 0.1f; 
                lastXPosition = bot.position.X;

                // Check death
                if (!bot.active || bot.dead) {
                    if (!wasDead) {
                        reward -= 10.0f;
                        isDone = true;
                    }
                }
                wasDead = bot.dead || !bot.active;

                // --- 3. SEND DATA ---
                
                // A. Send Stats (44 Floats) using the BOT index
                float[] stats = RLInterface.GetObservation(targetIndex);
                foreach (float val in stats) writer.Write(val);

                // B. Send Vision Grid (1600 Floats) using the BOT player object
                float[] grid = GridSensors.GetObservationGrid(bot);
                foreach (float val in grid) writer.Write(val);

                // C. Send Reward & Done
                writer.Write(reward);
                writer.Write(isDone);

                writer.Flush();

                // --- 4. WAIT & READ ACTION ---
                int actionIndex = reader.ReadInt32();

                // --- 5. EXECUTE ---
                float[] actionParams = ConvertActionToParams(actionIndex);
                
                // Apply to the specific BOT index
                RLInterface.ApplyAction(targetIndex, actionParams);
            }
            catch (Exception e) 
            {
                Main.NewText("AI Disconnected: " + e.Message, 255, 100, 100);
                CleanupConnection();
            }
        }

        private float[] ConvertActionToParams(int action)
        {
            // [Left, Right, Up, Down, Jump, Hook, AimX, AimY]
            float[] p = new float[8]; 
            
            // 0 = Idle
            if (action == 1) p[0] = 1f; // Left
            if (action == 2) p[1] = 1f; // Right
            if (action == 3) p[4] = 1f; // Jump
            
            return p;
        }
    }
}