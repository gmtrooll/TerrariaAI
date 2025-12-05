using FirstMod_Sp.Common.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace FirstMod_Sp.Content.Items.Tools
{
	public class TimeTurner : ModItem
	{
		public override string Texture => "Terraria/Images/Item_" + ItemID.Stopwatch;

		public override void SetDefaults() {
			Item.width = 20;
			Item.height = 20;
			Item.useTime = 20;
			Item.useAnimation = 20;
			Item.useStyle = ItemUseStyleID.HoldUp;
			Item.value = 0;
			Item.rare = ItemRarityID.Orange;
			Item.autoReuse = true; 
		}

		public override bool AltFunctionUse(Player player) => true;

		public override bool? UseItem(Player player) {
			if (player.whoAmI != Main.myPlayer) return true;

			// RIGHT CLICK: Toggle Background Run
			if (player.altFunctionUse == 2) {
				// Use animation check to prevent spam-toggling every frame
				if (player.ItemAnimationJustStarted) {
					BackgroundRunSystem.RunInBackground = !BackgroundRunSystem.RunInBackground;
					
					string status = BackgroundRunSystem.RunInBackground ? "ENABLED" : "DISABLED";
					Color color = BackgroundRunSystem.RunInBackground ? Color.Green : Color.Red;
					
					Main.NewText($"Background Execution: {status}", color);
				}
				return true;
			}

			// LEFT CLICK: Set speed based on distance
			float distance = Vector2.Distance(player.Center, Main.MouseWorld);
			int targetSpeed = (int)(distance / 16f);

			if (targetSpeed < 1) targetSpeed = 1;
			if (targetSpeed > 10) targetSpeed = 10;

			if (PhysicsAccelerator.TimeScale != targetSpeed) {
				PhysicsAccelerator.TimeScale = targetSpeed;
				
				Color color = Color.Lerp(Color.LimeGreen, Color.Red, targetSpeed / 10f);
				Main.NewText($"Game Speed: {targetSpeed}x", color);
			}

			return true;
		}

		public override void AddRecipes() {
			CreateRecipe()
				.AddIngredient(ItemID.DirtBlock, 10)
				.AddTile(TileID.WorkBenches)
				.Register();
		}
	}
}