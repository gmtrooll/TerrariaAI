using Terraria;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Benchmark
{
    /// <summary>
    /// Keeps the world from interfering with a benchmark or physics check: enemy spawning is random, so an enemy
    /// bumping a bot in one run but not the next makes otherwise identical runs diverge.
    /// </summary>
    public class BenchIsolation : GlobalNPC
    {
        public static bool BlockSpawns;

        public override void EditSpawnRate(Player player, ref int spawnRate, ref int maxSpawns) {
            if (BlockSpawns) maxSpawns = 0;
        }

        /// <summary>Remove enemies (town NPCs stay), dropped items and projectiles.</summary>
        public static void ClearWorld() {
            foreach (NPC npc in Main.npc) {
                if (npc.active && !npc.townNPC) npc.active = false;
            }
            foreach (Item item in Main.item) item.active = false;
            foreach (Projectile projectile in Main.projectile) projectile.active = false;
        }
    }
}
