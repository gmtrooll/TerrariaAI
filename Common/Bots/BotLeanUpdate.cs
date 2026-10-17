using System;
using FirstMod_Sp.Common.Systems;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Bots
{
    /// <summary>
    /// "Lean" bot updates (method M4 in docs/Throughput and Automation Plan.md). Bots keep the local-player path of
    /// Player.Update, so movement, damage, buffs and item use stay exactly a real player's, but skip the parts of it
    /// that only serve a human at this computer. Each skipped method was found in a dotnet-trace profile of bot
    /// updates and checked in the decompiled source:
    ///   TriggersSet.CopyInto        copies the keyboard/gamepad into the player and runs ModPlayer.ProcessTriggers;
    ///                               bots get their controls in SetControls (BotInputOverride) anyway
    ///   Player.dropItemCheck        cursor item, trash, guide and reforge slots of the inventory UI
    ///   ItemCheckWrapped's clone    lastVisualizedSelectedItem = HeldItem.Clone() on every idle tick, for drawing;
    ///                               ItemCheck_Inner re-clones it when an item use starts with a different item
    ///   UpdateNearbyInteractibleProjectilesList   "right-click to interact" prompts
    ///   UpdateDyes                  dye shader ids for drawing
    ///   UpdateBiomes, ForceUpdateBiomes   screen filters and sky; the zone flags they set came from
    ///                               Main.SceneMetrics, which is scanned around the real local player, not the bot
    /// Off by default; enable with -botlean or at runtime (BotLeanUpdate.Enabled = true via execute_csharp).
    /// </summary>
    public class BotLeanUpdate : ModSystem
    {
        public static bool Enabled = Program.LaunchParameters.ContainsKey("-botlean");

        private static bool Skip(Player player) => Enabled && BotManagerSystem.IsBot[player.whoAmI];

        public override void Load() {
            On_TriggersSet.CopyInto += CopyInto;
            On_Player.dropItemCheck += DropItemCheck;
            On_Player.UpdateNearbyInteractibleProjectilesList += UpdateNearbyInteractibleProjectilesList;
            On_Player.UpdateDyes += UpdateDyes;
            On_Player.UpdateBiomes += UpdateBiomes;
            On_Player.ForceUpdateBiomes += ForceUpdateBiomes;
            IL_Player.ItemCheckWrapped += SkipIdleHeldItemClone;
        }

        public override void Unload() {
            On_TriggersSet.CopyInto -= CopyInto;
            On_Player.dropItemCheck -= DropItemCheck;
            On_Player.UpdateNearbyInteractibleProjectilesList -= UpdateNearbyInteractibleProjectilesList;
            On_Player.UpdateDyes -= UpdateDyes;
            On_Player.UpdateBiomes -= UpdateBiomes;
            On_Player.ForceUpdateBiomes -= ForceUpdateBiomes;
            IL_Player.ItemCheckWrapped -= SkipIdleHeldItemClone;
        }

        private static void CopyInto(On_TriggersSet.orig_CopyInto orig, TriggersSet self, Player p) {
            if (!Skip(p)) orig(self, p);
        }

        private static void DropItemCheck(On_Player.orig_dropItemCheck orig, Player self) {
            if (!Skip(self)) orig(self);
        }

        private static void UpdateNearbyInteractibleProjectilesList(On_Player.orig_UpdateNearbyInteractibleProjectilesList orig, Player self) {
            if (!Skip(self)) orig(self);
        }

        private static void UpdateDyes(On_Player.orig_UpdateDyes orig, Player self) {
            if (!Skip(self)) orig(self);
        }

        private static void UpdateBiomes(On_Player.orig_UpdateBiomes orig, Player self) {
            if (!Skip(self)) orig(self);
        }

        private static void ForceUpdateBiomes(On_Player.orig_ForceUpdateBiomes orig, Player self) {
            if (!Skip(self)) orig(self);
        }

        /// <summary>
        /// ItemCheckWrapped ends with `if (itemAnimation == 0) lastVisualizedSelectedItem = HeldItem.Clone();`.
        /// Replace the Clone call with one that keeps the previous copy for lean bots. Item.Clone is costly in
        /// tModLoader because it also clones every mod's per-item data.
        /// </summary>
        private void SkipIdleHeldItemClone(ILContext il) {
            var c = new ILCursor(il);
            if (!c.TryGotoNext(MoveType.Before,
                    i => i.MatchCallOrCallvirt<Item>(nameof(Item.Clone)),
                    i => i.MatchStfld<Player>(nameof(Player.lastVisualizedSelectedItem)))) {
                Mod.Logger.Warn("[BotLean] ItemCheckWrapped: HeldItem.Clone() not found; idle clone not skipped");
                return;
            }
            c.Remove();
            c.Emit(OpCodes.Ldarg_0);
            c.EmitDelegate<Func<Item, Player, Item>>((held, player) =>
                Skip(player) && player.lastVisualizedSelectedItem != null ? player.lastVisualizedSelectedItem : held.Clone());
        }
    }
}
