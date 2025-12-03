using FirstMod_Sp.Common.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace FirstMod_Sp.Content.Items.Tools
{
	public class BotSpawner : ModItem
	{
		public override string Texture => "Terraria/Images/Item_" + ItemID.GoblinBattleStandard;

		public override void SetDefaults() {
			Item.width = 28;
			Item.height = 28;
			Item.useTime = 20;
			Item.useAnimation = 20;
			Item.useStyle = ItemUseStyleID.Swing;
			Item.value = 0;
			Item.rare = ItemRarityID.Red;
			Item.autoReuse = false;
		}

		public override bool AltFunctionUse(Player player) => true;

		public override bool? UseItem(Player player) {
			if (player.whoAmI != Main.myPlayer) return true;

			// RIGHT CLICK: Remove All Bots
			if (player.altFunctionUse == 2) {
				BotManagerSystem.RemoveAllBots();
				return true;
			}

			// LEFT CLICK: Spawn Bot
			BotManagerSystem.SpawnBot(Main.MouseWorld);
			return true;
		}

		public override void AddRecipes() {
			CreateRecipe()
				.AddIngredient(ItemID.DirtBlock, 1)
				.AddTile(TileID.WorkBenches)
				.Register();
		}
	}
}