using FirstMod_Sp.Content.Projectiles;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace FirstMod_Sp.Content.Items.Tools
{
	public class TargetSpawner : ModItem
	{
		// Use vanilla "Magic Mirror" texture as placeholder
		public override string Texture => "Terraria/Images/Item_" + ItemID.MagicMirror;

		public override void SetDefaults() {
			Item.width = 20;
			Item.height = 20;
			Item.useTime = 20;
			Item.useAnimation = 20;
			Item.useStyle = ItemUseStyleID.HoldUp;
			Item.value = 0;
			Item.rare = ItemRarityID.Green;
			Item.autoReuse = false;
		}

		// Allow Right-Clicking
		public override bool AltFunctionUse(Player player) => true;

		public override bool? UseItem(Player player) {
			if (player.whoAmI != Main.myPlayer) return true; // Only run on local client

			// Calculate where to spawn
			Vector2 spawnPos = Main.MouseWorld;
			int type = ModContent.ProjectileType<TargetMarkerProjectile>();

			// Step 1: Clear existing targets (so we only ever have one)
			for (int i = 0; i < Main.maxProjectiles; i++) {
				if (Main.projectile[i].active && 
					Main.projectile[i].type == type && 
					Main.projectile[i].owner == player.whoAmI) {
					
					Main.projectile[i].Kill();
				}
			}

			// Step 2: If Left Click (AltFunction 0), spawn new one
			if (player.altFunctionUse != 2) {
				Projectile.NewProjectile(
					player.GetSource_ItemUse(Item), 
					spawnPos, 
					Vector2.Zero, 
					type, 
					0, 
					0, 
					player.whoAmI
				);
				Main.NewText("Target Set!", 100, 255, 100);
			} else {
				Main.NewText("Target Removed.", 255, 100, 100);
			}

			return true;
		}

		public override void AddRecipes() {
			CreateRecipe()
				.AddIngredient(ItemID.DirtBlock, 5)
				.AddTile(TileID.WorkBenches)
				.Register();
		}
	}
}