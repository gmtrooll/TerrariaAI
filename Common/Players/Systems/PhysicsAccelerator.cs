using Terraria;
using Terraria.ModLoader;
using Microsoft.Xna.Framework;

namespace FirstMod_Sp.Common.Systems
{
	public class PhysicsAccelerator : ModSystem
	{
		// Modify this static variable from your AI code to speed up the game.
		// Example: PhysicsAccelerator.TimeScale = 10;
		public static int TimeScale = 1;

		public override void Load() {
			// We hook into "DoUpdate", which is the method Terraria uses to process
			// one "Tick" of logic (movement, projectiles, AI, etc.)
			On_Main.DoUpdate += FastForwardGameLoop;
		}

		public override void Unload() {
			On_Main.DoUpdate -= FastForwardGameLoop;
		}

		// FIX: Added 'ref' keyword to gameTime to match the delegate signature
		private void FastForwardGameLoop(On_Main.orig_DoUpdate orig, Main self, ref GameTime gameTime) {
			// Safety check to prevent infinite freezing if set to 0 or negative
			if (TimeScale < 1) TimeScale = 1;

			// Run the physics/logic update loop multiple times per single visual frame
			for (int i = 0; i < TimeScale; i++) {
				orig(self, ref gameTime);
			}
		}
	}
}