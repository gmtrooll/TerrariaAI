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
        public bool ControlLeft;
        public bool ControlRight;
        public bool ControlJump; 
        public bool ControlDown; 
        public bool ControlHook; 
        public Vector2 AimDirection; 
    }

    public class BotInputOverride : ModPlayer
    {
        public override void SetControls() {
            if (Player.whoAmI >= 0 && Player.whoAmI < 256 && BotManagerSystem.IsBot[Player.whoAmI]) {
                // 1. Reset hardware inputs
                Player.controlLeft = false;
                Player.controlRight = false;
                Player.controlJump = false;
                Player.controlDown = false;
                Player.controlUp = false;
                Player.controlUseItem = false;
                Player.controlHook = false;
                Player.controlMount = false;
                // Removed Player.controlSmart as requested

                // 2. Inject Bot inputs
                if (BotManagerSystem.BotStates.TryGetValue(Player.whoAmI, out var data)) {
                    if (data.ControlLeft) Player.controlLeft = true;
                    if (data.ControlRight) Player.controlRight = true;
                    if (data.ControlJump) Player.controlJump = true;
                    if (data.ControlDown) Player.controlDown = true;
                    if (data.ControlHook) Player.controlHook = true;
                    
                    // Note: No manual Left/Right conflict resolution needed; 
                    // Terraria engine handles opposing inputs naturally.
                }
            }
        }
    }

    public class BotManagerSystem : ModSystem
    {
        public static List<int> ActiveBots = new List<int>();
        public static Dictionary<int, BotData> BotStates = new Dictionary<int, BotData>();
        public static bool[] IsBot = new bool[256]; 
        public const int MAX_BOTS = 50;

        public override void Load() {
            On_Player.Update += SkipEngineUpdateForBots;
            On_Player.SavePlayer += SkipSave;
            On_AchievementsHelper.HandleRunning += SkipAchievements;
            On_CombatText.NewText_Rectangle_Color_string_bool_bool += SkipCombatText;
        }

        public override void Unload() {
            On_Player.Update -= SkipEngineUpdateForBots;
            On_Player.SavePlayer -= SkipSave;
            On_AchievementsHelper.HandleRunning -= SkipAchievements;
            On_CombatText.NewText_Rectangle_Color_string_bool_bool -= SkipCombatText;
            
            ActiveBots.Clear();
            BotStates.Clear();
            Array.Clear(IsBot, 0, IsBot.Length);
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
            if (IsBot[i] && Main.myPlayer != i) return; 
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

                Main.myPlayer = botIndex;

                // --- APPLY AIMING ---
                if (data.AimDirection != Vector2.Zero) {
                    // Calculate Aim relative to screen position
                    Vector2 aimScreenPos = bot.Center - Main.screenPosition + data.AimDirection;
                    Main.mouseX = (int)aimScreenPos.X;
                    Main.mouseY = (int)aimScreenPos.Y;
                } else {
                    // Default aim (forward)
                    Vector2 defaultAim = bot.Center - Main.screenPosition + new Vector2(bot.direction * 100, 0);
                    Main.mouseX = (int)defaultAim.X;
                    Main.mouseY = (int)defaultAim.Y;
                }

                bot.Update(botIndex);
            }

            Main.myPlayer = realPlayerID;
        }

        public static int SpawnBot(Vector2 position) {
            if (ActiveBots.Count >= MAX_BOTS) return -1;

            int slot = -1;
            for (int i = 254; i > 0; i--) {
                if (!Main.player[i].active) {
                    slot = i;
                    break;
                }
            }

            if (slot == -1) return -1;

            Main.player[slot] = new Player();
            Player bot = Main.player[slot];
            
            bot.active = true;
            bot.whoAmI = slot;
            bot.name = $"Bot_{slot}";
            bot.skinVariant = 0;
            bot.width = 20;
            bot.height = 42;
            bot.Center = position;
            bot.fallStart = (int)(position.Y / 16f);
            bot.fallStart2 = (int)(position.Y / 16f);
            bot.statLife = 100;
            bot.statLifeMax = 100;
            bot.dead = false;

            BotData data = new BotData();
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