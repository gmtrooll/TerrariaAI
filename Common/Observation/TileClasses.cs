using System;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Observation
{
    /// <summary>
    /// Maps every tile type to a small "class": the set of gameplay properties the observation cares about.
    /// The game has no single category field; behaviour is spread over static per-type tables (Main.tileSolid,
    /// TileID.Sets.*, see docs/Observation Design.md). Types with identical properties share a class, so a cell
    /// needs one byte, and the learner expands the class id into features with this table (sent once, via the
    /// observation_spec command). Class 0 is "no block".
    /// </summary>
    public class TileClasses : ModSystem
    {
        public static readonly string[] FeatureNames = {
            "solid", "platform", "rope", "door_closed", "door_open",
            "contact_damage", "hot", "trap", "suffocate",
            "slippery", "sticky", "bouncy", "conveyor", "no_fall_damage", "breaks_under", "falling",
            "tree", "axe", "hammer", "ore_value", "interactable", "furniture",
        };

        /// <summary>Tile types that the Dangersense potion highlights regardless of the player (traps, explosives).
        /// TileDrawing.IsTileDangerous; boulders come from TileID.Sets.Boulders.</summary>
        private static readonly HashSet<int> TrapTypes = new HashSet<int> {
            TileID.PressurePlates, TileID.Traps, TileID.Explosives, TileID.LandMine, TileID.ProjectilePressurePad,
            TileID.GeyserTrap, TileID.BeeHive, TileID.Detonator, TileID.AntlionLarva, TileID.TNTBarrel,
        };

        /// <summary>Class id per tile type.</summary>
        public static byte[] ClassOf = Array.Empty<byte>();
        /// <summary>Feature vector per class (FeatureNames order).</summary>
        public static float[][] Classes = Array.Empty<float[]>();

        // Per-type quick flags for the builder's hot loop.
        public static bool[] Solid = Array.Empty<bool>();
        public static bool[] Platform = Array.Empty<bool>();
        public static bool[] Hazard = Array.Empty<bool>();
        public static bool[] Rope = Array.Empty<bool>();

        public override void PostSetupContent() {
            Build();
        }

        public static void Build() {
            int count = TileLoader.TileCount;
            ClassOf = new byte[count];
            Solid = new bool[count];
            Platform = new bool[count];
            Hazard = new bool[count];
            Rope = new bool[count];

            var classes = new List<float[]> { new float[FeatureNames.Length] };  // class 0: no block
            var index = new Dictionary<string, int>();
            for (int type = 0; type < count; type++) {
                float[] f = Features(type);
                string key = string.Join(",", f);
                if (!index.TryGetValue(key, out int id)) {
                    id = classes.Count;
                    if (id > byte.MaxValue) {
                        ModContent.GetInstance<TileClasses>().Mod.Logger.Warn($"[Observation] more than 255 tile classes; type {type} shares class 255");
                        id = byte.MaxValue;
                    }
                    else {
                        classes.Add(f);
                        index[key] = id;
                    }
                }
                ClassOf[type] = (byte)id;
                Solid[type] = f[0] > 0;
                Platform[type] = f[1] > 0;
                Hazard[type] = f[5] > 0 || f[6] > 0 || f[7] > 0;
                Rope[type] = f[2] > 0;
            }
            Classes = classes.ToArray();
            ModContent.GetInstance<TileClasses>().Mod.Logger.Info($"[Observation] {count} tile types -> {Classes.Length} classes");
        }

        private static float[] Features(int t) {
            bool solidTop = Main.tileSolidTop[t];
            return new[] {
                B(Main.tileSolid[t] && !solidTop),
                B(solidTop),
                B(Main.tileRope[t]),
                B(TileID.Sets.OpenDoorID[t] != -1),   // a closed door: it has an open version
                B(TileID.Sets.CloseDoorID[t] != -1),  // an open door
                Math.Min(1f, TileID.Sets.TouchDamageImmediate[t] / 100f),
                B(TileID.Sets.TouchDamageHot[t]),
                B(TrapTypes.Contains(t) || TileID.Sets.Boulders[t]),
                B(TileID.Sets.Suffocate[t]),
                B(TileID.Sets.IceSkateSlippery[t] || t == TileID.FrozenSlimeBlock),
                B(t == TileID.HoneyBlock || t == TileID.Cobweb),
                B(Main.tileBouncy[t]),
                TileID.Sets.ConveyorDirection[t],
                B(TileID.Sets.NegatesFallDamage[t]),
                B(t == TileID.BreakableIce || TileID.Sets.CrackedBricks[t]),
                B(TileID.Sets.Falling[t]),
                B(TileID.Sets.IsATreeTrunk[t]),
                B(Main.tileAxe[t]),
                B(Main.tileHammer[t]),
                Math.Min(1f, Main.tileOreFinderPriority[t] / 1000f),
                B(Main.tileContainer[t] || TileID.Sets.InteractibleByNPCs[t] || TileID.Sets.CanBeSleptIn[t] || Main.tileSign[t]),
                B(Main.tileFrameImportant[t]),
            };
        }

        private static float B(bool value) => value ? 1f : 0f;

        public static Dictionary<int, Dictionary<string, float>> Describe() {
            return Enumerable.Range(0, Classes.Length).ToDictionary(
                id => id,
                id => FeatureNames.Select((name, i) => (name, value: Classes[id][i])).Where(p => p.value != 0)
                    .ToDictionary(p => p.name, p => p.value));
        }
    }
}
