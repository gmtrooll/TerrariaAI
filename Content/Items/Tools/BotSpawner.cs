using FirstMod_Sp.Common.Systems;
using FirstMod_Sp.Content.Projectiles; // Import Projectiles to find TargetMarker
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input; 
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
        
        public override void ModifyTooltips(System.Collections.Generic.List<TooltipLine> tooltips)
        {
            // Load config to show current bot count
            TrainingConfigLoader.LoadConfig();
            int botCount = TrainingConfigLoader.NumBots;
            
            tooltips.Add(new TooltipLine(Mod, "BotCount", $"[c/FFFF00:Ctrl + Click] - Spawn {botCount} bots"));
            tooltips.Add(new TooltipLine(Mod, "Controls1", "[c/00FF00:Left Click] - Spawn 1 bot"));
            tooltips.Add(new TooltipLine(Mod, "Controls2", "[c/FF0000:Right Click] - Remove all bots & targets"));
        }

        public override bool? UseItem(Player player) {
            if (player.whoAmI != Main.myPlayer) return true;

            // RIGHT CLICK: Remove All Bots AND Targets
            if (player.altFunctionUse == 2) {
                BotManagerSystem.RemoveAllBots();

                // Clear Targets
                int targetType = ModContent.ProjectileType<TargetMarkerProjectile>();
                for (int i = 0; i < Main.maxProjectiles; i++) {
                    if (Main.projectile[i].active && 
                        Main.projectile[i].type == targetType && 
                        Main.projectile[i].owner == player.whoAmI) 
                    {
                        Main.projectile[i].Kill();
                    }
                }
                
                Main.NewText("Cleared Bots and Targets.", 255, 100, 100);
                return true;
            }

            // LEFT CLICK HANDLING
            // Check if Control is held down
            if (Main.keyState.IsKeyDown(Keys.LeftControl) || Main.keyState.IsKeyDown(Keys.RightControl)) {
                
                // Load config to get bot count
                TrainingConfigLoader.LoadConfig();
                int botCount = TrainingConfigLoader.NumBots;
                
                // Spawn bots based on config
                for (int i = 0; i < botCount; i++) {
                    // Add a small random offset so they don't spawn inside each other
                    Vector2 offset = Main.rand.NextVector2Circular(32, 0); 
                    BotManagerSystem.SpawnBot(Main.MouseWorld + offset);
                }
                Main.NewText($"Spawned {botCount} Bots!", 100, 255, 100);
            
            } else {
                // Standard: Spawn 1 Bot
                BotManagerSystem.SpawnBot(Main.MouseWorld);
            }

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