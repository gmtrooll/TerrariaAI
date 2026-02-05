using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace FirstMod_Sp.Content.Items.Tools
{
    /// <summary>
    /// Tool to toggle mob spawning globally for training purposes.
    /// Click to toggle on/off.
    /// </summary>
    public class MobSpawnToggle : ModItem
    {
        // Use a magic mirror texture as placeholder
        public override string Texture => "Terraria/Images/Item_" + ItemID.MagicMirror;

        // Static flag to track mob spawn state
        private static bool _mobSpawnsDisabled = false;
        public static bool MobSpawnsDisabled => _mobSpawnsDisabled;

        public override void SetStaticDefaults()
        {
            Item.ResearchUnlockCount = 1;
        }

        public override void SetDefaults()
        {
            Item.width = 28;
            Item.height = 28;
            Item.useTime = 20;
            Item.useAnimation = 20;
            Item.useStyle = ItemUseStyleID.HoldUp;
            Item.value = 0;
            Item.rare = ItemRarityID.Pink;
            Item.UseSound = SoundID.Item4;
        }

        public override void ModifyTooltips(System.Collections.Generic.List<TooltipLine> tooltips)
        {
            string status = _mobSpawnsDisabled ? "[c/00FF00:DISABLED]" : "[c/FF0000:ENABLED]";
            tooltips.Add(new TooltipLine(Mod, "Status", $"Mob Spawning: {status}"));
            tooltips.Add(new TooltipLine(Mod, "Controls", "[c/FFFF00:Click] - Toggle mob spawning"));
        }

        public override bool? UseItem(Player player)
        {
            if (player.whoAmI != Main.myPlayer) return true;

            _mobSpawnsDisabled = !_mobSpawnsDisabled;

            if (_mobSpawnsDisabled)
            {
                Main.NewText("Mob spawning DISABLED for training!", 100, 255, 100);
            }
            else
            {
                Main.NewText("Mob spawning ENABLED!", 255, 100, 100);
            }

            return true;
        }

        public override void AddRecipes()
        {
            CreateRecipe()
                .AddIngredient(ItemID.DirtBlock, 1)
                .Register();
        }
    }

    // Global NPC class to actually disable spawning
    public class MobSpawnToggleGlobal : GlobalNPC
    {
        public override void EditSpawnRate(Player player, ref int spawnRate, ref int maxSpawns)
        {
            // If mob spawns are disabled, set spawn rate to effectively 0
            if (MobSpawnToggle.MobSpawnsDisabled)
            {
                spawnRate = 999999;  // Very high = very slow spawns
                maxSpawns = 0;       // No spawns allowed
            }
        }
    }
}
