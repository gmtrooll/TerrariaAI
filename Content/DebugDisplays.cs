using Microsoft.Xna.Framework;
using FirstMod_Sp.Common.Players;
using FirstMod_Sp.Content.Projectiles; // Needed to see the TargetMarkerProjectile
using Terraria;
using Terraria.ModLoader;
using Terraria.DataStructures; // REQUIRED for ExtraJumpState
using System;
using System.Text; // REQUIRED for StringBuilder

namespace FirstMod_Sp.Content
{
    // =================================================================================
    // 1. PHYSICS DISPLAY
    // Shows: Velocity, Direction, Gravity Direction
    // =================================================================================
    public class DebugPhysicsDisplay : InfoDisplay
    {
        public override string Texture => "Terraria/Images/UI/InfoIcon_3"; // Stopwatch

        public override bool Active() => Main.LocalPlayer.GetModPlayer<DebugInfoPlayer>().showDebugInfo;

        public override string DisplayValue(ref Color displayColor, ref Color displayShadowColor) {
            Player p = Main.LocalPlayer;
            // Inputs: 1, 2, 9
            return $"Vel: {p.velocity.X:F1}, {p.velocity.Y:F1} | Dir: {p.direction} | Grav: {p.gravDir}";
        }
    }

    // =================================================================================
    // 2. BODY STATS DISPLAY
    // Shows: Position (Center), Size, Run Speeds
    // =================================================================================
    public class DebugBodyDisplay : InfoDisplay
    {
        public override string Texture => "Terraria/Images/UI/InfoIcon_4"; // Compass

        public override bool Active() => Main.LocalPlayer.GetModPlayer<DebugInfoPlayer>().showDebugInfo;

        public override string DisplayValue(ref Color displayColor, ref Color displayShadowColor) {
            Player p = Main.LocalPlayer;
            // Inputs: 4, 5, 11, 12
            // Note: accRunSpeed is the current "max" run speed based on boots/surface
            return $"Pos: {p.Center.X:F0},{p.Center.Y:F0} | Size: {p.width}x{p.height} | MaxSpd: {p.accRunSpeed:F1} | Acc: {p.runAcceleration:F2}";
        }
    }

    // =================================================================================
    // 3. ENVIRONMENT & STATE DISPLAY
    // Shows: Liquids and Control Locks (Frozen/Webbed/Stoned)
    // =================================================================================
    public class DebugStateDisplay : InfoDisplay
    {
        public override string Texture => "Terraria/Images/UI/InfoIcon_0"; // Weather Radio

        public override bool Active() => Main.LocalPlayer.GetModPlayer<DebugInfoPlayer>().showDebugInfo;

        public override string DisplayValue(ref Color displayColor, ref Color displayShadowColor) {
            Player p = Main.LocalPlayer;
            
            // Inputs: 3 (Liquids)
            string liquid = "Dry";
            if (p.wet) liquid = "Wet";
            if (p.honeyWet) liquid = "Honey";
            if (p.lavaWet) liquid = "Lava";

            // Inputs: 16 (Crowd Control)
            string cc = "None";
            if (p.frozen) cc = "Frozen";
            if (p.webbed) cc = "Webbed";
            if (p.stoned) cc = "Stoned";

            if (liquid != "Dry" || cc != "None") {
                displayColor = new Color(255, 100, 100); // Highlight danger
            }

            return $"Env: {liquid} | State: {cc}";
        }
    }

    // =================================================================================
    // 4. AIR & RESOURCES DISPLAY
    // Shows: Fall Tiles, Flight Time, Rocket Time, Hooks
    // =================================================================================
    public class DebugAirDisplay : InfoDisplay
    {
        public override string Texture => "Terraria/Images/UI/InfoIcon_1"; // Depth Meter

        public override bool Active() => Main.LocalPlayer.GetModPlayer<DebugInfoPlayer>().showDebugInfo;

        public override string DisplayValue(ref Color displayColor, ref Color displayShadowColor) {
            Player p = Main.LocalPlayer;

            // Input 10: Fall Distance (Tiles)
            int fallDist = (int)(p.position.Y / 16f) - p.fallStart2;

            // Input 15: Grappling Hooks
            int hooksActive = 0;
            for (int i = 0; i < p.grappling.Length; i++) {
                if (p.grappling[i] >= 0) hooksActive++;
            }

            // Inputs: 13, 14 (Wing/Rocket Time)
            return $"Fall: {fallDist} | Wing: {p.wingTime:F0}/{p.wingTimeMax:F0} | Rkt: {p.rocketTime:F0}/{p.rocketTimeMax:F0} | Hooks: {hooksActive}";
        }
    }

    // =================================================================================
    // 5. JUMP DISPLAY (NEW)
    // Shows: Base Jump frames and status of Extra Jumps (Cloud, Sandstorm, etc.)
    // =================================================================================
    public class DebugJumpDisplay : InfoDisplay
    {
        public override string Texture => "Terraria/Images/UI/InfoIcon_5"; // Tally Counter

        public override bool Active() => Main.LocalPlayer.GetModPlayer<DebugInfoPlayer>().showDebugInfo;

        public override string DisplayValue(ref Color displayColor, ref Color displayShadowColor) {
            Player p = Main.LocalPlayer;
            StringBuilder sb = new StringBuilder();

            // 1. Base Jump (The default jump)
            // p.jump is the frame counter for the current jump height. > 0 means currently moving up.
            sb.Append($"Base: {p.jump} | Ex: "); 

            // 2. Extra Jumps Checker
            // We verify the state of the standard Vanilla bottle jumps using the new 1.4.4 API.
            // + = Available (Ready to use)
            // * = Active (Currently propelling)
            // - = Used (Consumed)
            
            CheckJump(p, sb, ExtraJump.CloudInABottle, "Cld");
            CheckJump(p, sb, ExtraJump.SandstormInABottle, "Snd");
            CheckJump(p, sb, ExtraJump.BlizzardInABottle, "Blz");
            CheckJump(p, sb, ExtraJump.FartInAJar, "Frt");
            CheckJump(p, sb, ExtraJump.TsunamiInABottle, "Tsu");
            CheckJump(p, sb, ExtraJump.BasiliskMount, "Mnt"); // Mount double jump

            // If string ends with "Ex: ", no extra jumps are equipped
            string result = sb.ToString();
            if (result.EndsWith("Ex: ")) {
                return $"Base Jump: {p.jump}";
            }

            return result;
        }

        // Helper function to process ExtraJumpState
        private void CheckJump(Player p, StringBuilder sb, ExtraJump jump, string name) {
            // Get the struct state. Requires Terraria.DataStructures
            ExtraJumpState state = p.GetJumpState(jump);

            // Enabled means the accessory is equipped/effect is active
            if (state.Enabled) {
                sb.Append(name);
                if (state.Active) {
                    sb.Append("* "); // Currently jumping
                }
                else if (state.Available) {
                    sb.Append("+ "); // Available to use
                }
                else {
                    sb.Append("- "); // Already used
                }
            }
        }
    }

    // =================================================================================
    // 6. TARGETING DISPLAY
    // Shows: Relative data to the CUSTOM TARGET MARKER
    // =================================================================================
    public class DebugTargetDisplay : InfoDisplay
    {
        public override string Texture => "Terraria/Images/UI/InfoIcon_2"; // Radar

        public override bool Active() => Main.LocalPlayer.GetModPlayer<DebugInfoPlayer>().showDebugInfo;

        public override string DisplayValue(ref Color displayColor, ref Color displayShadowColor) {
            Player p = Main.LocalPlayer;
            
            // Find our custom projectile
            Projectile target = null;
            int targetType = ModContent.ProjectileType<TargetMarkerProjectile>();

            for (int i = 0; i < Main.maxProjectiles; i++) {
                Projectile proj = Main.projectile[i];
                if (proj.active && proj.type == targetType && proj.owner == p.whoAmI) {
                    target = proj;
                    break; 
                }
            }

            if (target == null) {
                return "Target: None (Use Spawner)";
            }

            // Inputs: 6, 7, 8 relative to Custom Target
            float distance = p.Distance(target.Center);
            float angle = p.AngleTo(target.Center); // Radians
            Vector2 dirVec = p.DirectionTo(target.Center); // Normalized Vector

            return $"Tgt: Custom | Dist: {distance:F0} | Ang: {angle:F2}rad | Dir: {dirVec.X:F1},{dirVec.Y:F1}";
        }
    }
}