using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using Terraria.DataStructures; 

namespace FirstMod_Sp.Common.Systems
{
    public class RLInterface : ModSystem
    {
        // =================================================================================
        // 1. OBSERVATION SPACE
        // Returns flattened data regarding physics, detailed jump states, flight, and hooks.
        // Total Size: 44 Floats
        // =================================================================================
        public static float[] GetObservation(int botIndex) {
            Player bot = Main.player[botIndex];
            
            // Size Breakdown:
            // 1. Physics (6)
            // 2. Body Stats (4) -> Added Width/Height back
            // 3. Jumps (19)
            // 4. Flight (3)
            // 5. Grapple (2)
            // 6. Environment (7)
            // 7. Fall Data (1)
            // 8. Survival (2)
            // Total: 44
            int obsSize = 44;
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
            // Sub-tile position (0-16)
            obs[idx++] = bot.Center.X % 16;
            obs[idx++] = bot.Center.Y % 16;

            // --- 2. BODY STATS (4) ---
            obs[idx++] = bot.accRunSpeed;       
            obs[idx++] = bot.runAcceleration;   
            // Restored Dimensions
            obs[idx++] = (float)bot.width;
            obs[idx++] = (float)bot.height;

            // --- 3. JUMPING DETAILED (19) ---
            obs[idx++] = bot.jump; 
            AddJumpObservation(bot, ExtraJump.CloudInABottle, ref idx, obs);
            AddJumpObservation(bot, ExtraJump.SandstormInABottle, ref idx, obs);
            AddJumpObservation(bot, ExtraJump.BlizzardInABottle, ref idx, obs);
            AddJumpObservation(bot, ExtraJump.FartInAJar, ref idx, obs);
            AddJumpObservation(bot, ExtraJump.TsunamiInABottle, ref idx, obs);
            AddJumpObservation(bot, ExtraJump.BasiliskMount, ref idx, obs);

            // --- 4. FLIGHT & ROCKETS (3) ---
            obs[idx++] = bot.wingTime;
            obs[idx++] = bot.wingTimeMax;
            obs[idx++] = bot.rocketTime;

            // --- 5. GRAPPLING HOOKS (2) ---
            obs[idx++] = bot.grapCount > 0 ? 1f : 0f; 
            obs[idx++] = bot.grapCount;        

            // --- 6. ENVIRONMENT & STATE (7) ---
            // Liquid states
            obs[idx++] = bot.wet ? 1f : 0f;
            obs[idx++] = bot.honeyWet ? 1f : 0f;
            obs[idx++] = bot.lavaWet ? 1f : 0f;
            // Crowd Control states
            obs[idx++] = bot.frozen ? 1f : 0f;
            obs[idx++] = bot.webbed ? 1f : 0f;
            obs[idx++] = bot.stoned ? 1f : 0f;
            // Movement State
            obs[idx++] = bot.pulley ? 1f : 0f; 

            // --- 7. FALL DATA (1) --- 
            float fallDist = (bot.position.Y / 16f) - bot.fallStart2;
            obs[idx++] = fallDist;

            // --- 8. SURVIVAL (2) ---
            obs[idx++] = (float)bot.statLife / (float)bot.statLifeMax;
            obs[idx++] = (float)bot.immuneTime;

            return obs;
        }

        private static void AddJumpObservation(Player bot, ExtraJump jump, ref int idx, float[] obs) {
            ExtraJumpState state = bot.GetJumpState(jump);
            obs[idx++] = state.Enabled ? 1f : 0f;   // Equipped
            obs[idx++] = state.Available ? 1f : 0f; // Can use
            obs[idx++] = state.Active ? 1f : 0f;    // Currently Using
        }

        // =================================================================================
        // 2. ACTION SPACE
        // [0] = Left, [1] = Right, [2] = Up, [3] = Down, [4] = Jump, [5] = Hook
        // [6] = Aim X, [7] = Aim Y
        // =================================================================================
        public static void ApplyAction(int botIndex, float[] actions) {
            if (!BotManagerSystem.BotStates.TryGetValue(botIndex, out BotData data)) return;

            // Movement Inputs
            if (actions.Length >= 6) {
                data.ControlLeft    = actions[0] > 0.5f;
                data.ControlRight   = actions[1] > 0.5f;
                data.ControlUp      = actions[2] > 0.5f;
                data.ControlDown    = actions[3] > 0.5f;
                data.ControlJump    = actions[4] > 0.5f;
                data.ControlHook    = actions[5] > 0.5f;
            }

            // Aiming Inputs
            if (actions.Length >= 8) {
                if (actions[6] != 0 || actions[7] != 0) {
                    data.AimDirection = new Vector2(actions[6], actions[7]);
                } else {
                    data.AimDirection = Vector2.Zero;
                }
            }
        }
    }
}