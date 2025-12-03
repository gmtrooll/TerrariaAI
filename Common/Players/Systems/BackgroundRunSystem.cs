using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using System;
using Terraria;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Systems
{
	public class BackgroundRunSystem : ModSystem
	{
		// Master switch for background execution
		public static bool RunInBackground = false;

		public override void Load() {
			// 1. Hook the deep update loop to force 'hasFocus' to true
			Terraria.IL_Main.DoUpdate += MainDoUpdateHasFocus;
			
			// 2. Hook the pause check to prevent the menu from opening on Alt-Tab
			Terraria.On_Main.CanPauseGame += CanPauseGame;
		}

		public override void Unload() {
			Terraria.IL_Main.DoUpdate -= MainDoUpdateHasFocus;
			Terraria.On_Main.CanPauseGame -= CanPauseGame;
		}

		// --- IL HOOK ---
		// This modifies the game's compiled code in memory.
		// It finds the exact moment the game asks Windows "Do I have focus?"
		// and changes the answer to "Yes" if our flag is true.
		private void MainDoUpdateHasFocus(ILContext il) {
			try {
				ILCursor c = new ILCursor(il);

				// Search for the instruction where 'Main.hasFocus' is set
				if (!c.TryGotoNext(MoveType.Before, i => i.MatchStsfld(typeof(Main), nameof(Main.hasFocus)))) {
					return; // Failed to find the instruction
				}

				// Inject our logic: 
				// ValueOnStack = ValueOnStack | RunInBackground
				c.EmitDelegate(() => RunInBackground);
				c.Emit(OpCodes.Or);
			}
			catch (Exception e) {
				Mod.Logger.Error("BackgroundRun IL Hook failed: " + e.Message);
			}
		}

		// --- DETOUR HOOK ---
		// Forces the game to never pause (e.g. "Autopause") while this mode is active
		private bool CanPauseGame(On_Main.orig_CanPauseGame orig) {
			if (RunInBackground)
				return false;

			return orig();
		}
	}
}