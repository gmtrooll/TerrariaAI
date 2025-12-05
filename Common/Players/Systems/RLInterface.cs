using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using Terraria.DataStructures;
using FirstMod_Sp.Content.Projectiles; // Required for TargetMarkerProjectile

namespace FirstMod_Sp.Common.Systems
{
    public class RLInterface : ModSystem
    {
        // UPDATED SIZE: 44 -> 46
        private static float[] _cachedObservation = new float[46];

        public static float[] GetObservation(int playerIndex) {
            Player p = Main.player[playerIndex];
            float[] obs = _cachedObservation;

            if (p == null || !p.active || p.dead) {
                Array.Clear(obs, 0, obs.Length);
                return obs;
            }

            int idx = 0;

            // --- 1. PHYSICS (6) ---
            obs[idx++] = MathHelper.Clamp(p.velocity.X / 10f, -10f, 10f); // Normalized Velocity
            obs[idx++] = MathHelper.Clamp(p.velocity.Y / 10f, -10f, 10f);
            obs[idx++] = (float)p.direction; 
            obs[idx++] = (float)p.gravDir;
            obs[idx++] = p.Center.X % 16 / 16f; // Normalized Sub-tile
            obs[idx++] = p.Center.Y % 16 / 16f;

            // --- 2. BODY STATS (4) ---
            obs[idx++] = p.accRunSpeed;       
            obs[idx++] = p.runAcceleration;   
            obs[idx++] = (float)p.width;
            obs[idx++] = (float)p.height;

            // --- 3. JUMPING (19) ---
            obs[idx++] = (float)p.jump; 
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
            obs[idx++] = (float)p.grapCount;        

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
            obs[idx++] = MathHelper.Clamp(fallDist / 50f, -10f, 10f); // Normalized fall

            // --- 8. SURVIVAL (2) ---
            float maxLife = (float)p.statLifeMax;
            if (maxLife <= 0f) maxLife = 1f; 
            obs[idx++] = MathHelper.Clamp((float)p.statLife / maxLife, 0f, 1f);
            obs[idx++] = (float)p.immuneTime;

            // --- 9. TARGET INFO (2) --- [NEW]
            Vector2 targetDelta = Vector2.Zero;
            int targetType = ModContent.ProjectileType<TargetMarkerProjectile>();
            
            for (int i = 0; i < Main.maxProjectiles; i++) {
                if (Main.projectile[i].active && Main.projectile[i].type == targetType) {
                    targetDelta = Main.projectile[i].Center - p.Center;
                    break;
                }
            }
            
            // NORMALIZATION FIX:
            // Neural nets fail if inputs are too big (like 500.0).
            // We divide by 16 (blocks) and then clamp. 
            // 20 blocks range is enough for "precise" movement.
            // If target is further than 20 blocks, the AI just sees "20" (Max Direction).
            obs[idx++] = MathHelper.Clamp(targetDelta.X / 16f, -20f, 20f); 
            obs[idx++] = MathHelper.Clamp(targetDelta.Y / 16f, -20f, 20f);

            // FINAL SAFETY PASS
            for (int i = 0; i < obs.Length; i++) {
                if (float.IsNaN(obs[i]) || float.IsInfinity(obs[i])) obs[i] = 0f;
            }

            return obs;
        }

        private static void AddJumpObservation(Player p, ExtraJump jump, ref int idx, float[] obs) {
            ExtraJumpState state = p.GetJumpState(jump);
            obs[idx++] = state.Enabled ? 1f : 0f;   
            obs[idx++] = state.Available ? 1f : 0f; 
            obs[idx++] = state.Active ? 1f : 0f;    
        }

        public static void ApplyAction(int playerIndex, float[] actions) {
            if (BotManagerSystem.BotStates.TryGetValue(playerIndex, out BotData data)) {
                // MultiBinary Logic:
                // actions[0] = Left
                // actions[1] = Right
                // actions[2] = Jump
                // (We ignore Up/Down/Hook for now to simplify learning)
                
                if (actions.Length >= 3) {
                    data.ControlLeft    = actions[0] > 0.5f;
                    data.ControlRight   = actions[1] > 0.5f;
                    data.ControlJump    = actions[2] > 0.5f;
                }
            }
        }
    }
}