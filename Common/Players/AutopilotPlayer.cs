/*using FirstMod_Sp.Content.Projectiles;
using Terraria;
using Terraria.ModLoader;
using Microsoft.Xna.Framework;

namespace FirstMod_Sp.Common.Players
{
	public class AutopilotPlayer : ModPlayer
	{
		public override void SetControls() {
			// Check if the Debug Monitor is equipped by accessing the DebugInfoPlayer
			// This ensures the autopilot only runs when the item is equipped.
			if (Player.GetModPlayer<DebugInfoPlayer>().showDebugInfo) {
				MoveTowardsTarget();
			}
		}

		private void MoveTowardsTarget() {
			// 1. Find the Custom Target Projectile
			Projectile target = null;
			int targetType = ModContent.ProjectileType<TargetMarkerProjectile>();

			for (int i = 0; i < Main.maxProjectiles; i++) {
				Projectile proj = Main.projectile[i];
				// Check if active, correct type, and owned by this player
				if (proj.active && proj.type == targetType && proj.owner == Player.whoAmI) {
					target = proj;
					break; 
				}
			}

			// 2. If no target exists, do nothing (allow manual control)
			if (target == null) return;

			// 3. Calculate Direction
			Vector2 directionVector = Player.DirectionTo(target.Center);

			// 4. Apply Movement (Simple A/D Logic)
			
			// Deadzone: If we are very close (within 1 tile), stop moving to prevent jittering
			float distance = Player.Distance(target.Center);
			if (distance < 16f) {
				Player.controlLeft = false;
				Player.controlRight = false;
				Player.velocity.X *= 0.9f; 
				return;
			}

			// Check X component of the direction vector
			if (directionVector.X < 0) {
				// Target is to the LEFT
				Player.controlLeft = true;
				Player.controlRight = false;
			} 
			else if (directionVector.X > 0) {
				// Target is to the RIGHT
				Player.controlLeft = false;
				Player.controlRight = true;
			}
		}
	}
}
*/