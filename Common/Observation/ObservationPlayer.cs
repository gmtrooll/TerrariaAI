using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Observation
{
    /// <summary>
    /// Per-player values the observation needs that cannot be read after the tick:
    /// - Player.jumpHeight and Player.jumpSpeed are static (one value for all players), overwritten during every
    ///   player's update (Player.cs:2878, 2883); gravity is overwritten mid-tick by featherfall (Player.cs:25839).
    ///   They are captured here, inside this player's own update, after UpdateJumpHeight and the floor modifiers.
    /// - The controls actually applied this tick (the previous action, part of the state: a jump needs a release
    ///   first, a dash is a double tap).
    /// Also holds the per-bot goal and the "seen" memory for visibility mode d.
    /// </summary>
    public class ObservationPlayer : ModPlayer
    {
        public int JumpHeight = 15;
        public float JumpSpeed = 5.01f;
        public float Gravity = 0.4f;
        public float MaxFallSpeed = 10f;

        public bool LastLeft, LastRight, LastUp, LastDown, LastJump;
        /// <summary>Decisions in a row pressing left or right while barely moving (ObservationConfig.Blocked).</summary>
        public int BlockedDecisions;
        /// <summary>Measured abilities of the current loadout (Env.Loadouts features), shown with -obs gear=1.</summary>
        public float[] GearFeatures;

        /// <summary>Goal position in world pixels, or null when the skill has none.</summary>
        public Vector2? Target;
        /// <summary>Next waypoint of the planner's route (body-centre point), set by the skill each decision.</summary>
        public Vector2? Waypoint;
        /// <summary>Near waypoint (a few route tiles ahead), when ObservationConfig.NearWaypoint is on.</summary>
        public Vector2? NearWaypoint;

        /// <summary>Visibility mode d: one bit per world tile, index y + x * maxTilesY.</summary>
        public ulong[] SeenBits;

        /// <summary>The overview grid, refreshed every ObservationConfig.CoarseEvery observations.</summary>
        public byte[] CoarseCache;
        public int CoarseAge;
        public int CoarseVersion = -1;
        public int CoarseCenterX = int.MinValue / 2, CoarseCenterY = int.MinValue / 2;

        public override void PostUpdateRunSpeeds() {
            JumpHeight = Terraria.Player.jumpHeight;
            JumpSpeed = Terraria.Player.jumpSpeed;
            Gravity = Player.gravity;
            MaxFallSpeed = Player.maxFallSpeed;
            LastLeft = Player.controlLeft;
            LastRight = Player.controlRight;
            LastUp = Player.controlUp;
            LastDown = Player.controlDown;
            LastJump = Player.controlJump;
        }

        public override void OnEnterWorld() {
            SeenBits = null;
            CoarseCache = null;
        }
    }
}
