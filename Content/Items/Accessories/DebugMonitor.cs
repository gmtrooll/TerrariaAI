using FirstMod_Sp.Common.Players;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace FirstMod_Sp.Content.Items.Accessories
{
	public class DebugMonitor : ModItem
	{
		// FIX: Use the Vanilla "Depth Meter" texture to prevent crash
		public override string Texture => "Terraria/Images/Item_" + ItemID.DepthMeter;

		public override void SetDefaults() {
			// Clones basic stats from Depth Meter so it behaves like a standard info accessory
			Item.CloneDefaults(ItemID.DepthMeter);
			Item.value = 0; // Cheap
			Item.rare = ItemRarityID.Blue;
		}

		// This hook enables the boolean in our ModPlayer
		public override void UpdateInfoAccessory(Player player) {
			player.GetModPlayer<DebugInfoPlayer>().showDebugInfo = true;
		}

		// Crafting Recipe: 1 Dirt Block @ Workbench
		public override void AddRecipes() {
			CreateRecipe()
				.AddIngredient(ItemID.DirtBlock, 1)
				.AddTile(TileID.WorkBenches)
				.Register();
		}
	}
}