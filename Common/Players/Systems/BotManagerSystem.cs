using System;
using Microsoft.Xna.Framework;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.IO;
using Terraria.GameContent.Achievements;

namespace FirstMod_Sp.Common.Systems
{
    public class BotData {
        public Vector2 Position;
        public Vector2 Velocity;
        public int FallStart;
        public int FallStart2;
        public bool ControlLeft;
        public bool ControlRight;
        public bool ControlJump;
        public bool ControlDown;
    }

    public class BotInputOverride : ModPlayer
    {
        public override void SetControls() {
            // Safety check: Ensure index is valid and it is marked as a bot
            if (Player.whoAmI >= 0 && Player.whoAmI < 256 && BotManagerSystem.IsBot[Player.whoAmI]) {
                // 1. Wipe all inputs first
                Player.controlLeft = false;
                Player.controlRight = false;
                Player.controlJump = false;
                Player.controlDown = false;
                Player.controlUp = false;
                Player.controlUseItem = false;
                Player.controlHook = false;
                Player.controlMount = false;

                // 2. Apply bot specific inputs
                if (BotManagerSystem.BotStates.TryGetValue(Player.whoAmI, out var data)) {
                    if (data.ControlLeft) Player.controlLeft = true;
                    if (data.ControlRight) Player.controlRight = true;
                    if (data.ControlJump) Player.controlJump = true;
                    if (data.ControlDown) Player.controlDown = true;
                }
            }
        }
    }

    public class BotManagerSystem : ModSystem
    {
        public static List<int> ActiveBots = new List<int>();
        public static Dictionary<int, BotData> BotStates = new Dictionary<int, BotData>();
        
        // Fast lookup array
        public static bool[] IsBot = new bool[256]; 
        
        public const int MAX_BOTS = 50;

        public override void Load() {
            On_Player.Update += SkipEngineUpdateForBots;
            On_Player.SavePlayer += SkipSave;
            On_AchievementsHelper.HandleRunning += SkipAchievements;
            On_CombatText.NewText_Rectangle_Color_string_bool_bool += SkipCombatText;
            On_Player.UpdateBiomes += InjectBotInputs;
        }

        public override void Unload() {
            On_Player.Update -= SkipEngineUpdateForBots;
            On_Player.SavePlayer -= SkipSave;
            On_AchievementsHelper.HandleRunning -= SkipAchievements;
            On_CombatText.NewText_Rectangle_Color_string_bool_bool -= SkipCombatText;
            On_Player.UpdateBiomes -= InjectBotInputs;
            
            ActiveBots.Clear();
            BotStates.Clear();
            Array.Clear(IsBot, 0, IsBot.Length);
        }

        private void InjectBotInputs(On_Player.orig_UpdateBiomes orig, Player self) {
            orig(self);
            
            // Double-check inputs here in case other mods interfere during Biome updates
            if (self.whoAmI >= 0 && self.whoAmI < 256 && IsBot[self.whoAmI]) {
                if (BotStates.TryGetValue(self.whoAmI, out var data)) {
                    self.controlLeft = data.ControlLeft;
                    self.controlRight = data.ControlRight;
                    self.controlJump = data.ControlJump;
                    self.controlDown = data.ControlDown;
                    
                    if (data.ControlLeft) self.controlRight = false;
                    if (data.ControlRight) self.controlLeft = false;
                }
            }
        }

        private void SkipSave(On_Player.orig_SavePlayer orig, PlayerFileData playerFile, bool skipMapSave) {
            if (ActiveBots.Count > 0) return; 
            if (playerFile?.Player != null && IsBot[playerFile.Player.whoAmI]) return;
            orig(playerFile, skipMapSave);
        }

        private void SkipAchievements(On_AchievementsHelper.orig_HandleRunning orig, float pixelsMoved) {
            if (IsBot[Main.myPlayer]) return;
            orig(pixelsMoved);
        }

        private int SkipCombatText(On_CombatText.orig_NewText_Rectangle_Color_string_bool_bool orig, Rectangle location, Color color, string text, bool dramatic, bool dot) {
            if (IsBot[Main.myPlayer]) return 100;
            return orig(location, color, text, dramatic, dot);
        }

        private void SkipEngineUpdateForBots(On_Player.orig_Update orig, Player self, int i) {
            if (IsBot[i]) {
                // If the bot is the "current player" (during our loop), let it update.
                // Otherwise, if the engine tries to update it normally, skip it 
                // (unless we want double updates, but usually we control it manually).
                if (Main.myPlayer != i) return; 
            }
            orig(self, i);
        }

        public override void PostUpdatePlayers() {
            UpdateBots();
        }

        private void UpdateBots() {
            int realPlayerID = Main.myPlayer;
            int count = ActiveBots.Count; 

            for (int i = 0; i < count; i++) {
                int botIndex = ActiveBots[i];
                Player bot = Main.player[botIndex];

                if (!bot.active || bot.dead) {
                    bot.velocity = Vector2.Zero;
                    continue;
                }

                if (!BotStates.TryGetValue(botIndex, out BotData data)) {
                    data = new BotData();
                    BotStates[botIndex] = data;
                }

                // --- AI LOGIC (Placeholder) ---
                data.ControlLeft = false;
                data.ControlRight = false;
                data.ControlJump = false;
                data.ControlDown = false;

                // Simple behavior for testing
                if (i == 0) data.ControlLeft = true;
                else if (i == 1) data.ControlRight = true;
                else if (i == 2 && bot.velocity.Y == 0) data.ControlJump = true;
                else if (i == 3) data.ControlDown = true;
                // ----------------

                // Set Main.myPlayer to the bot so logic thinks it's the local player
                Main.myPlayer = botIndex;
                bot.Update(botIndex);
                
                // Save state back
                data.Position = bot.position;
                data.Velocity = bot.velocity;
                data.FallStart = bot.fallStart;
                data.FallStart2 = bot.fallStart2;
            }

            // Restore Real Player
            Main.myPlayer = realPlayerID;
        }

        public static int SpawnBot(Vector2 position) {
            if (ActiveBots.Count >= MAX_BOTS) return -1;

            int slot = -1;
            // Find empty slot (iterate backwards to avoid low slots usually taken by host/NPCs)
            for (int i = 254; i > 0; i--) {
                if (!Main.player[i].active) {
                    slot = i;
                    break;
                }
            }

            if (slot == -1) return -1;

            // 1. Create a fresh player object. This clears inventory, buffs, etc. automatically.
            Main.player[slot] = new Player();
            Player bot = Main.player[slot];
            
            // 2. Initialize Identity
            bot.active = true;
            bot.whoAmI = slot;
            bot.name = $"Bot_{slot}";
            bot.skinVariant = 0;
            
            // 3. Initialize Physics/Position
            // Set dimensions explicitly before setting Center
            bot.width = 20;
            bot.height = 42;
            bot.Center = position;
            
            bot.fallStart = (int)(position.Y / 16f);
            bot.fallStart2 = (int)(position.Y / 16f);
            
            // 4. Initialize Stats
            bot.statLife = 100;
            bot.statLifeMax = 100;
            bot.dead = false;

            // 5. Initialize Bot System Data
            BotData data = new BotData();
            data.Position = position;
            data.Velocity = Vector2.Zero;
            data.FallStart = bot.fallStart;
            data.FallStart2 = bot.fallStart2;
            
            BotStates[slot] = data;
            ActiveBots.Add(slot);
            IsBot[slot] = true;
            
            return slot;
        }

        public static void RemoveAllBots() {
            for (int i = ActiveBots.Count - 1; i >= 0; i--) {
                int index = ActiveBots[i];
                Main.player[index].active = false;
                IsBot[index] = false;
            }
            ActiveBots.Clear();
            BotStates.Clear();
            Main.NewText("All bots removed.");
        }
    }
}