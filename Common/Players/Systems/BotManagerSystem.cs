using System;
using Microsoft.Xna.Framework;
using System.Collections.Generic;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.IO;
using Terraria.GameContent.Achievements;
using Terraria.Localization; // Required for NetworkText

namespace FirstMod_Sp.Common.Systems
{
    public class BotData {
        public bool ControlLeft;
        public bool ControlRight;
        public bool ControlUp;   
        public bool ControlDown; 
        public bool ControlJump; 
        public bool ControlHook; 
        public bool ControlUseItem; 
        public Vector2 AimDirection; 

        public void Reset() {
            ControlLeft = false;
            ControlRight = false;
            ControlUp = false;
            ControlDown = false;
            ControlJump = false;
            ControlHook = false;
            ControlUseItem = false;
            AimDirection = Vector2.Zero;
        }
    }

    public class BotInputOverride : ModPlayer
    {
        public override void SetControls() {
            // 1. Handle FAKE BOTS
            if (Player.whoAmI >= 0 && Player.whoAmI < 256 && BotManagerSystem.IsBot[Player.whoAmI]) {
                ResetInputs();
                if (BotManagerSystem.BotStates.TryGetValue(Player.whoAmI, out var data)) {
                    ApplyData(data);
                }
            }
        }

        // --- NEW FEATURE: INSTANT RESPAWN ---
        public override void UpdateDead() {
            // If this is a bot, force the timer to 0 to respawn immediately
            if (Player.whoAmI >= 0 && Player.whoAmI < 256 && BotManagerSystem.IsBot[Player.whoAmI]) {
                Player.respawnTimer = 0;
            }
        }

        private void ResetInputs() {
            Player.controlLeft = false;
            Player.controlRight = false;
            Player.controlUp = false;
            Player.controlDown = false;
            Player.controlJump = false;
            Player.controlUseItem = false;
            Player.controlHook = false;
            Player.controlMount = false;
        }

        private void ApplyData(BotData data) {
            if (data.ControlLeft) Player.controlLeft = true;
            if (data.ControlRight) Player.controlRight = true;
            if (data.ControlUp) Player.controlUp = true;
            if (data.ControlDown) Player.controlDown = true;
            if (data.ControlJump) Player.controlJump = true;
            if (data.ControlHook) Player.controlHook = true;
            if (data.ControlUseItem) Player.controlUseItem = true;
        }
    }

    public class BotManagerSystem : ModSystem
    {
        public static List<int> ActiveBots = new List<int>();
        public static Dictionary<int, BotData> BotStates = new Dictionary<int, BotData>();
        
        public static BotData LocalPlayerOverride = new BotData(); 
        
        public static bool[] IsBot = new bool[256]; 
        public const int MAX_BOTS = 50;

        public override void Load() {
            On_Player.Update += SkipEngineUpdateForBots;
            On_Player.SavePlayer += SkipSave;
            On_AchievementsHelper.HandleRunning += SkipAchievements;
            On_CombatText.NewText_Rectangle_Color_string_bool_bool += SkipCombatText;
            On_Projectile.Update += EnableBotProjectileAI;
            
            // --- NEW HOOKS ---
            On_Player.DropTombstone += PreventBotTombstones;
            On_Player.Spawn += PreserveBotPositionOnSpawn;
        }

        public override void Unload() {
            On_Player.Update -= SkipEngineUpdateForBots;
            On_Player.SavePlayer -= SkipSave;
            On_AchievementsHelper.HandleRunning -= SkipAchievements;
            On_CombatText.NewText_Rectangle_Color_string_bool_bool -= SkipCombatText;
            On_Projectile.Update -= EnableBotProjectileAI;
            On_Player.DropTombstone -= PreventBotTombstones;
            On_Player.Spawn -= PreserveBotPositionOnSpawn;
            
            ActiveBots.Clear();
            BotStates.Clear();
            LocalPlayerOverride = null;
            Array.Clear(IsBot, 0, IsBot.Length);
        }

        // --- RESPAWN WHERE DIED LOGIC ---
        private void PreserveBotPositionOnSpawn(On_Player.orig_Spawn orig, Player self, PlayerSpawnContext context) {
            bool isBot = self.whoAmI >= 0 && self.whoAmI < 256 && IsBot[self.whoAmI];
            
            // Capture position BEFORE spawn resets it to the bed/world spawn
            Vector2 deathPos = self.Center;
            
            // Run standard Spawn (Resets Health, Mana, Buffs, etc.)
            orig(self, context);
            
            if (isBot) {
                // If they died in a valid location, put them back there.
                // If they fell out of the world (Vector Zero or OOB), let them spawn at default to prevent loop.
                if (deathPos != Vector2.Zero && WorldGen.InWorld((int)deathPos.X / 16, (int)deathPos.Y / 16)) {
                    self.Center = deathPos;
                    self.velocity = Vector2.Zero;
                    self.fallStart = (int)(self.position.Y / 16f);
                }
            }
        }

        private void PreventBotTombstones(On_Player.orig_DropTombstone orig, Player self, long coinsOwned, NetworkText deathText, int hitDirection) {
            if (self.whoAmI >= 0 && self.whoAmI < 256 && IsBot[self.whoAmI]) {
                return;
            }
            orig(self, coinsOwned, deathText, hitDirection);
        }

        private void EnableBotProjectileAI(On_Projectile.orig_Update orig, Projectile self, int i) {
            if (self.active && self.owner >= 0 && self.owner < 256 && IsBot[self.owner]) {
                int realPlayer = Main.myPlayer;
                Main.myPlayer = self.owner;
                try {
                    orig(self, i);
                } finally {
                    Main.myPlayer = realPlayer;
                }
            } else {
                orig(self, i);
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
            if (IsBot[i] && Main.myPlayer != i) return; 
            orig(self, i);
        }

        public override void PostUpdatePlayers() {
            UpdateBots();
            if (LocalPlayerOverride != null) LocalPlayerOverride.Reset();
        }

        private void UpdateBots() {
            int realPlayerID = Main.myPlayer;
            int count = ActiveBots.Count; 

            for (int i = 0; i < count; i++) {
                int botIndex = ActiveBots[i];
                Player bot = Main.player[botIndex];

                // FIX: Do NOT skip if bot is dead. 
                // We must run Update() so the respawn timer logic can execute.
                if (!bot.active) {
                    continue;
                }

                if (!BotStates.TryGetValue(botIndex, out BotData data)) {
                    data = new BotData();
                    BotStates[botIndex] = data;
                }

                Main.myPlayer = botIndex;

                if (data.AimDirection != Vector2.Zero) {
                    Vector2 aimScreenPos = bot.Center - Main.screenPosition + data.AimDirection;
                    Main.mouseX = (int)aimScreenPos.X;
                    Main.mouseY = (int)aimScreenPos.Y;
                } else {
                    Vector2 defaultAim = bot.Center - Main.screenPosition + new Vector2(bot.direction * 200, -50);
                    Main.mouseX = (int)defaultAim.X;
                    Main.mouseY = (int)defaultAim.Y;
                }

                bot.Update(botIndex);
                data.Reset(); 
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
            bot.Spawn(PlayerSpawnContext.SpawningIntoWorld); 
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
            
            // --- INVENTORY ---
            bot.armor[3].SetDefaults(ItemID.TsunamiInABottle); 
            
            // NOTE: miscEquips[0] is the dedicated Hook Slot. 
            // If you found Slot 4 works, you might be referring to inventory[4]. 
            // We set BOTH just to be safe.
            bot.miscEquips[0].SetDefaults(ItemID.GrapplingHook); 
            bot.inventory[4].SetDefaults(ItemID.GrapplingHook); 

            bot.inventory[0].SetDefaults(ItemID.IronBroadsword); 
            bot.inventory[1].SetDefaults(ItemID.IronPickaxe);    
            
            bot.selectedItem = 0;

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