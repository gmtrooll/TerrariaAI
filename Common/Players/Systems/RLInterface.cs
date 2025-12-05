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
        // CACHED MEMORY: Created once, reused forever. 
        // Prevents Garbage Collector lag spikes.
        private static float[] _cachedObservation = new float[44];

        // =================================================================================
        // 1. OBSERVATION SPACE
        // =================================================================================
        public static float[] GetObservation(int playerIndex) {
            Player p = Main.player[playerIndex];
            
            // Use the cached array instead of 'new float[44]'
            float[] obs = _cachedObservation;

            // If player is dead/inactive, zero out the array and return
            if (!p.active || p.dead) {
                Array.Clear(obs, 0, obs.Length);
                return obs;
            }

            int idx = 0;

            // --- 1. PHYSICS (6) ---
            obs[idx++] = p.velocity.X;
            obs[idx++] = p.velocity.Y;
            obs[idx++] = p.direction; 
            obs[idx++] = p.gravDir;
            obs[idx++] = p.Center.X % 16; 
            obs[idx++] = p.Center.Y % 16;

            // --- 2. BODY STATS (4) ---
            obs[idx++] = p.accRunSpeed;       
            obs[idx++] = p.runAcceleration;   
            obs[idx++] = (float)p.width;
            obs[idx++] = (float)p.height;

            // --- 3. JUMPING (19) ---
            obs[idx++] = p.jump; 
            AddJumpObservation(p, ExtraJump.CloudInABottle, ref idx, obs);
            AddJumpObservation(p, ExtraJump.SandstormInABottle, ref idx, obs);
            AddJumpObservation(p, ExtraJump.BlizzardInABottle, ref idx, obs);
            AddJumpObservation(p, ExtraJump.FartInAJar, ref idx, obs);
            AddJumpObservation(p, ExtraJump.TsunamiInABottle, ref idx, obs);
            AddJumpObservation(p, ExtraJump.BasiliskMount, ref idx, obs);

            // --- 4. FLIGHT (3) ---
            obs[idx++] = p.wingTime;
            obs[idx++] = p.wingTimeMax;
            obs[idx++] = p.rocketTime;

            // --- 5. GRAPPLE (2) ---
            obs[idx++] = p.grapCount > 0 ? 1f : 0f; 
            obs[idx++] = p.grapCount;        

            // --- 6. ENVIRONMENT (7) ---
            obs[idx++] = p.wet ? 1f : 0f;
            obs[idx++] = p.honeyWet ? 1f : 0f;
            obs[idx++] = p.lavaWet ? 1f : 0f;
            obs[idx++] = p.frozen ? 1f : 0f;
            obs[idx++] = p.webbed ? 1f : 0f;
            obs[idx++] = p.stoned ? 1f : 0f;
            obs[idx++] = p.pulley ? 1f : 0f; 

            // --- 7. FALL (1) --- 
            float fallDist = (p.position.Y / 16f) - p.fallStart2;
            obs[idx++] = fallDist;

            // --- 8. SURVIVAL (2) ---
            obs[idx++] = (float)p.statLife / (float)p.statLifeMax;
            obs[idx++] = (float)p.immuneTime;

            return obs;
        }

        private static void AddJumpObservation(Player p, ExtraJump jump, ref int idx, float[] obs) {
            ExtraJumpState state = p.GetJumpState(jump);
            obs[idx++] = state.Enabled ? 1f : 0f;   
            obs[idx++] = state.Available ? 1f : 0f; 
            obs[idx++] = state.Active ? 1f : 0f;    
        }

        // =================================================================================
        // 2. ACTION APPLIER (Bots Only)
        // =================================================================================
        public static void ApplyAction(int playerIndex, float[] actions) {
            
            // We ONLY check the Bot Dictionary now.
            // We do not check for Real Player override.
            if (BotManagerSystem.BotStates.TryGetValue(playerIndex, out BotData data)) {
                
                if (actions.Length >= 6) {
                    data.ControlLeft    = actions[0] > 0.5f;
                    data.ControlRight   = actions[1] > 0.5f;
                    data.ControlUp      = actions[2] > 0.5f;
                    data.ControlDown    = actions[3] > 0.5f;
                    data.ControlJump    = actions[4] > 0.5f;
                    data.ControlHook    = actions[5] > 0.5f;
                }

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
}