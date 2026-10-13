using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;
using Terraria.DataStructures;

namespace FirstMod_Sp.Common.Systems
{
    public class RLInterface : ModSystem
    {
        // Observation layout (RLProtocol.OBS_VERSION 2). Mirror of Training/protocol.py STATS_FIELDS.
        // Everything is scaled to roughly [-1, 1]. Features that are constant for the training bots
        // (body size, run speed, gravity, unequipped jump accessories, wings, rockets, hooks) were removed.
        public const int STATS_SIZE = 23;

        // Scales
        private const float VELOCITY_SCALE = 10f;       // px/tick; max fall speed is 10
        private const float JUMP_FRAMES_SCALE = 20f;    // Player.jump counts down from jumpHeight (~15)
        private const float FALL_TILES_SCALE = 25f;     // fall damage starts at ~25 tiles
        private const float IMMUNE_FRAMES_SCALE = 60f;
        private const float TARGET_DELTA_TILES = 20f;   // delta is clamped to +/- this many tiles
        private static readonly float LogDistanceNorm = MathF.Log(1f + 128f); // log1p(dist) / log1p(128 tiles)

        // Shared buffer, overwritten by every call: callers must copy it before calling again.
        private static float[] _cachedObservation = new float[STATS_SIZE];

        /// <summary>
        /// Build the stats vector for one bot. The target is the bot's own zone target
        /// (or the global marker when it has no zone); pass hasTarget=false when there is none.
        /// </summary>
        public static float[] GetObservation(int playerIndex, bool hasTarget, Vector2 targetPos) {
            Player p = Main.player[playerIndex];
            float[] obs = _cachedObservation;
            Array.Clear(obs, 0, obs.Length);

            if (p == null || !p.active || p.dead) {
                return obs;
            }

            int idx = 0;

            // --- 1. PHYSICS (5) ---
            obs[idx++] = MathHelper.Clamp(p.velocity.X / VELOCITY_SCALE, -2f, 2f);
            obs[idx++] = MathHelper.Clamp(p.velocity.Y / VELOCITY_SCALE, -2f, 2f);
            obs[idx++] = p.direction;
            obs[idx++] = p.Center.X % 16 / 16f; // Sub-tile position
            obs[idx++] = p.Center.Y % 16 / 16f;

            // --- 2. JUMPING (3): base jump + the Tsunami in a Bottle the bots wear ---
            obs[idx++] = MathHelper.Clamp(p.jump / JUMP_FRAMES_SCALE, 0f, 1f);
            ExtraJumpState tsunami = p.GetJumpState(ExtraJump.TsunamiInABottle);
            obs[idx++] = tsunami.Available ? 1f : 0f;
            obs[idx++] = tsunami.Active ? 1f : 0f;

            // --- 3. ENVIRONMENT (6) ---
            obs[idx++] = p.wet ? 1f : 0f;
            obs[idx++] = p.honeyWet ? 1f : 0f;
            obs[idx++] = p.lavaWet ? 1f : 0f;
            obs[idx++] = p.frozen ? 1f : 0f;
            obs[idx++] = p.webbed ? 1f : 0f;
            obs[idx++] = p.stoned ? 1f : 0f;

            // --- 4. FALL (1) ---
            float fallTiles = (p.position.Y / 16f) - p.fallStart2;
            obs[idx++] = MathHelper.Clamp(fallTiles / FALL_TILES_SCALE, -1f, 1f);

            // --- 5. SURVIVAL (2) ---
            float maxLife = p.statLifeMax > 0 ? p.statLifeMax : 1f;
            obs[idx++] = MathHelper.Clamp(p.statLife / maxLife, 0f, 1f);
            obs[idx++] = MathHelper.Clamp(p.immuneTime / IMMUNE_FRAMES_SCALE, 0f, 1f);

            // --- 6. TARGET (6) ---
            if (hasTarget) {
                Vector2 deltaTiles = (targetPos - p.Center) / 16f;
                float distTiles = deltaTiles.Length();
                obs[idx++] = 1f;
                obs[idx++] = MathHelper.Clamp(deltaTiles.X, -TARGET_DELTA_TILES, TARGET_DELTA_TILES) / TARGET_DELTA_TILES;
                obs[idx++] = MathHelper.Clamp(deltaTiles.Y, -TARGET_DELTA_TILES, TARGET_DELTA_TILES) / TARGET_DELTA_TILES;
                // Unit direction and log distance stay meaningful beyond the clamp range.
                obs[idx++] = distTiles > 1e-4f ? deltaTiles.X / distTiles : 0f;
                obs[idx++] = distTiles > 1e-4f ? deltaTiles.Y / distTiles : 0f;
                obs[idx++] = MathHelper.Clamp(MathF.Log(1f + distTiles) / LogDistanceNorm, 0f, 1f);
            } else {
                idx += 6; // has_target = 0, rest 0
            }

            // FINAL SAFETY PASS
            for (int i = 0; i < obs.Length; i++) {
                if (float.IsNaN(obs[i]) || float.IsInfinity(obs[i])) obs[i] = 0f;
            }

            return obs;
        }

        /// <summary>Apply an action bitmask (bit0 left, bit1 right, bit2 jump) to a bot.</summary>
        public static void ApplyActionBits(int playerIndex, int actionBits) {
            if (BotManagerSystem.BotStates.TryGetValue(playerIndex, out BotData data)) {
                data.ControlLeft  = (actionBits & 1) != 0;
                data.ControlRight = (actionBits & 2) != 0;
                data.ControlJump  = (actionBits & 4) != 0;
            }
        }
    }
}
