using FirstMod_Sp.Common.Systems;
using FirstMod_Sp.Content.Projectiles;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace FirstMod_Sp.Content.Items.Tools
{
    public class BotTeleporter : ModItem
    {
        // Using Rod of Discord texture as it fits the "Teleport" theme
        public override string Texture => "Terraria/Images/Item_" + ItemID.RodofDiscord;

        public override void SetDefaults() {
            Item.width = 28;
            Item.height = 28;
            Item.useTime = 15;
            Item.useAnimation = 15;
            Item.useStyle = ItemUseStyleID.Swing;
            Item.value = 0;
            Item.rare = ItemRarityID.Orange;
            Item.autoReuse = false;
        }

        public override bool AltFunctionUse(Player player) => true;

        public override bool? UseItem(Player player) {
            if (player.whoAmI != Main.myPlayer) return true;

            // RIGHT CLICK: Move Target
            if (player.altFunctionUse == 2) {
                MoveTarget(player);
                return true;
            }

            // LEFT CLICK: Teleport All Bots
            TeleportBots();
            return true;
        }

        private void TeleportBots() {
            Vector2 targetPos = Main.MouseWorld;
            int count = 0;

            foreach (int botIndex in BotManagerSystem.ActiveBots) {
                Player bot = Main.player[botIndex];
                
                if (bot != null && bot.active && !bot.dead) {
                    // Spread them slightly (16px radius) to avoid physics glitching
                    Vector2 offset = Main.rand.NextVector2Circular(16, 0);
                    
                    bot.Center = targetPos + offset;
                    
                    // Reset momentum so they don't die on impact
                    bot.velocity = Vector2.Zero;
                    bot.fallStart = (int)(bot.position.Y / 16f);
                    
                    count++;
                }
            }
            
            if (count > 0) {
                Main.NewText($"Teleported {count} bots.", 50, 255, 255);
            }
        }

        private void MoveTarget(Player player) {
            int targetType = ModContent.ProjectileType<TargetMarkerProjectile>();
            bool found = false;

            // 1. Search for existing target
            for (int i = 0; i < Main.maxProjectiles; i++) {
                Projectile p = Main.projectile[i];
                if (p.active && p.type == targetType && p.owner == player.whoAmI) {
                    p.Center = Main.MouseWorld;
                    p.netUpdate = true; // Sync position
                    found = true;
                    // We found it, stop looking.
                    break;
                }
            }

            // 2. If no target exists, create one
            if (!found) {
                Projectile.NewProjectile(
                    player.GetSource_ItemUse(Item), 
                    Main.MouseWorld, 
                    Vector2.Zero, 
                    targetType, 
                    0, 
                    0, 
                    player.whoAmI
                );
            }
            
            Main.NewText("Target Moved.", 255, 100, 255);
        }

        public override void AddRecipes() {
            CreateRecipe()
                .AddIngredient(ItemID.DirtBlock, 1)
                .AddTile(TileID.WorkBenches)
                .Register();
        }
    }
}