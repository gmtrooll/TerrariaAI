using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Systems
{
    public class RLInterface : ModSystem
    {
        // =================================================================================
        // 1. OBSERVATION SPACE
        // Returns flattened data.
        // Size: 18 Floats
        // =================================================================================
        public static float[] GetObservation(int botIndex) {
            Player bot = Main.player[botIndex];
            
            int obsSize = 18;
            float[] obs = new float[obsSize];

            if (!bot.active || bot.dead) {
                return obs; 
            }

            int idx = 0;

            // --- 1. BASIC PHYSICS (6) ---
            obs[idx++] = bot.velocity.X;
            obs[idx++] = bot.velocity.Y;
            obs[idx++] = (float)bot.direction; 
            obs[idx++] = (float)bot.gravDir;
            obs[idx++] = bot.Center.X;
            obs[idx++] = bot.Center.Y;

            // --- 2. JUMPING & WINGS (5) ---
            // Current jump counter
            obs[idx++] = (float)bot.jump; 
            
            // Extra Jumps (Double Jumps)
            // Iterating the ExtraJumps span to count how many are actually Available
            int availableExtraJumps = 0;
            foreach (var jumpState in bot.ExtraJumps) {
                if (jumpState.Available) {
                    availableExtraJumps++;
                }
            }
            obs[idx++] = (float)availableExtraJumps;

            // Flight resources
            obs[idx++] = bot.wingTime;
            obs[idx++] = bot.wingTimeMax;
            
            // Rocket boots (Time Left)
            obs[idx++] = bot.rocketTime;

            // --- 3. GRAPPLING HOOKS (2) ---
            // 'grapCount' is the reliable internal counter for active hooks
            obs[idx++] = bot.grapCount > 0 ? 1f : 0f; // Boolean: Is Grappling?
            obs[idx++] = (float)bot.grapCount;        // Count: How many hooks?

            // --- 4. ENVIRONMENT & STATE (5) ---
            obs[idx++] = bot.wet ? 1f : 0f;
            obs[idx++] = bot.mount.Active ? 1f : 0f;
            obs[idx++] = bot.frozen ? 1f : 0f;
            obs[idx++] = bot.webbed ? 1f : 0f;
            obs[idx++] = bot.stoned ? 1f : 0f;

            return obs;
        }

        // =================================================================================
        // 2. ACTION SPACE
        // Applies inputs to BotData. 
        // Array Size: 7 (Added AimX and AimY)
        // [0] = Left
        // [1] = Right
        // [2] = Jump 
        // [3] = Down 
        // [4] = Hook 
        // [5] = Aim X Offset (Relative to player)
        // [6] = Aim Y Offset (Relative to player)
        // =================================================================================
        public static void ApplyAction(int botIndex, float[] actions) {
            if (!BotManagerSystem.BotStates.TryGetValue(botIndex, out BotData data)) return;

            // Movement Inputs
            if (actions.Length >= 5) {
                data.ControlLeft    = actions[0] > 0.5f;
                data.ControlRight   = actions[1] > 0.5f;
                data.ControlJump    = actions[2] > 0.5f;
                data.ControlDown    = actions[3] > 0.5f;
                data.ControlHook    = actions[4] > 0.5f;
            }

            // Aiming Inputs
            if (actions.Length >= 7) {
                // If the AI outputs aim (e.g., 100, -50), we set it here.
                // If it outputs 0,0, we leave AimDirection as Zero (UpdateBots will default it to forward)
                if (actions[5] != 0 || actions[6] != 0) {
                    data.AimDirection = new Vector2(actions[5], actions[6]);
                } else {
                    data.AimDirection = Vector2.Zero;
                }
            }
        }
    }
}