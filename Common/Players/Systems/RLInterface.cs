using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using Terraria.DataStructures; // Required for ExtraJumpState

namespace FirstMod_Sp.Common.Systems
{
    public class RLInterface : ModSystem
    {
        // =================================================================================
        // 1. OBSERVATION SPACE
        // Returns flattened data regarding physics, detailed jump states, flight, and hooks.
        // Total Size: 35 Floats
        // =================================================================================
        public static float[] GetObservation(int botIndex) {
            Player bot = Main.player[botIndex];
            
            // Physics (6) + BaseJump (1) + 6 Jumps * 3 stats (18) + Flight (3) + Grapple (2) + Env (5) = 35
            int obsSize = 35;
            float[] obs = new float[obsSize];

            if (!bot.active || bot.dead) {
                return obs; 
            }

            int idx = 0;

            // --- 1. BASIC PHYSICS (6) ---
            obs[idx++] = bot.velocity.X;
            obs[idx++] = bot.velocity.Y;
            obs[idx++] = bot.direction; 
            obs[idx++] = bot.gravDir;
            obs[idx++] = bot.Center.X;
            obs[idx++] = bot.Center.Y;

            // --- 2. JUMPING DETAILED (19) ---
            // Base Jump Height Counter (0 = grounded/not jumping)
            obs[idx++] = bot.jump; 

            // Add detailed stats for each Vanilla Extra Jump type:
            // 1. Enabled (Equipped)
            // 2. Available (Charge ready)
            // 3. Active (Currently boosting)
            
            AddJumpObservation(bot, ExtraJump.CloudInABottle, ref idx, obs);
            AddJumpObservation(bot, ExtraJump.SandstormInABottle, ref idx, obs);
            AddJumpObservation(bot, ExtraJump.BlizzardInABottle, ref idx, obs);
            AddJumpObservation(bot, ExtraJump.FartInAJar, ref idx, obs);
            AddJumpObservation(bot, ExtraJump.TsunamiInABottle, ref idx, obs);
            AddJumpObservation(bot, ExtraJump.BasiliskMount, ref idx, obs);

            // --- 3. FLIGHT & ROCKETS (3) ---
            obs[idx++] = bot.wingTime;
            obs[idx++] = bot.wingTimeMax;
            obs[idx++] = bot.rocketTime;

            // --- 4. GRAPPLING HOOKS (2) ---
            obs[idx++] = bot.grapCount > 0 ? 1f : 0f; 
            obs[idx++] = bot.grapCount;        

            // --- 5. ENVIRONMENT & STATE (5) ---
            obs[idx++] = bot.wet ? 1f : 0f;
            obs[idx++] = bot.mount.Active ? 1f : 0f;
            obs[idx++] = bot.frozen ? 1f : 0f;
            obs[idx++] = bot.webbed ? 1f : 0f;
            obs[idx++] = bot.stoned ? 1f : 0f;

            return obs;
        }

        // Helper to flatten ExtraJumpState into 3 floats
        private static void AddJumpObservation(Player bot, ExtraJump jump, ref int idx, float[] obs) {
            ExtraJumpState state = bot.GetJumpState(jump);
            obs[idx++] = state.Enabled ? 1f : 0f;   // Equipped
            obs[idx++] = state.Available ? 1f : 0f; // Can use
            obs[idx++] = state.Active ? 1f : 0f;    // Currently Using
        }

        // =================================================================================
        // 2. ACTION SPACE
        // [0] = Left, [1] = Right, [2] = Jump, [3] = Down, [4] = Hook
        // [5] = Aim X, [6] = Aim Y
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
                if (actions[5] != 0 || actions[6] != 0) {
                    data.AimDirection = new Vector2(actions[5], actions[6]);
                } else {
                    data.AimDirection = Vector2.Zero;
                }
            }
        }
    }
}