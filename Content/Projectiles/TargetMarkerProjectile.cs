using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace FirstMod_Sp.Content.Projectiles
{
	public class TargetMarkerProjectile : ModProjectile
	{
		// Use a high-visibility vanilla texture (The "Ruby Bolt" projectile is bright red)
		public override string Texture => "Terraria/Images/Projectile_" + ProjectileID.RubyBolt;

		public override void SetDefaults() {
			Projectile.width = 16;
			Projectile.height = 16;
			Projectile.friendly = false;
			Projectile.hostile = false;
			Projectile.tileCollide = false; // Don't hit walls
			Projectile.ignoreWater = true;
			Projectile.timeLeft = 360000; // Last for 10 minutes
			Projectile.penetrate = -1;
		}

		public override void AI() {
			// Stay completely still
			Projectile.velocity = Microsoft.Xna.Framework.Vector2.Zero;
			
			// Make it rotate slightly so it looks cool
			Projectile.rotation += 0.05f;
		}
	}
}