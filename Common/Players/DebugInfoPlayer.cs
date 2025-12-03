using Terraria;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Players
{
	public class DebugInfoPlayer : ModPlayer
	{
		// Flag to check if the accessory is equipped
		public bool showDebugInfo;

		public override void ResetInfoAccessories() {
			showDebugInfo = false;
		}

		public override void RefreshInfoAccessoriesFromTeamPlayers(Player otherPlayer) {
			if (otherPlayer.GetModPlayer<DebugInfoPlayer>().showDebugInfo) {
				showDebugInfo = true;
			}
		}
	}
}