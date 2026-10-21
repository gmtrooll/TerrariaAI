using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Terraria;
using Terraria.ID;

namespace FirstMod_Sp.Common.Env
{
    /// <summary>
    /// Equipment per loadout code (the scenario's 6th int). Built in: 0 = nothing, 1 = Tsunami in a Bottle. With
    /// -envloadouts FILE (Training/loadouts.json, written by navigate.gear_measure) the table is read from the file:
    ///   [{ "code": 2, "name": "cloud", "items": ["CloudinaBottle"], "jump_tiles": 9, "features": [ ... ] }, ...]
    /// Items are ItemID names (ItemID.Search), worn in accessory slots 3-7. jump_tiles is the planner's jump height for
    /// the loadout (measured total apex, rounded down). features are the measured abilities the observation shows with
    /// -obs gear=1 (ObservationBuilder.GearNames): describing gear by what it does, not by item id, so an item never
    /// seen in training is a new combination of known numbers.
    /// </summary>
    public static class Loadouts
    {
        public const int FirstSlot = 3, Slots = 5, FeatureCount = 4;

        public sealed class Entry
        {
            public int Code, JumpTiles;
            public string Name;
            public int[] Items = Array.Empty<int>();
            public float[] Features = new float[FeatureCount];
        }

        private static Dictionary<int, Entry> table;
        public static readonly string FilePath = Program.LaunchParameters.TryGetValue("-envloadouts", out string lp) ? lp : null;

        private static Dictionary<int, Entry> Table {
            get {
                if (table != null) return table;
                var t = new Dictionary<int, Entry> {
                    [NavigateEnv.LoadoutNone] = new Entry { Code = 0, Name = "none", JumpTiles = 6, Features = new[] { 6.13f, 6.13f, 0f, 3f } },
                    [NavigateEnv.LoadoutTsunami] = new Entry { Code = 1, Name = "tsunami", JumpTiles = NavigateEnv.MaxJumpTiles, Items = new int[] { ItemID.TsunamiInABottle },
                        Features = new[] { 6.13f, 13.12f, 1f, 3f } },
                };
                if (FilePath != null && File.Exists(FilePath)) {
                    foreach (JObject o in JArray.Parse(File.ReadAllText(FilePath))) {
                        var e = new Entry { Code = (int)o["code"], Name = (string)o["name"], JumpTiles = (int)o["jump_tiles"] };
                        var items = new List<int>();
                        foreach (var n in o["items"] ?? new JArray()) {
                            if (!ItemID.Search.TryGetId((string)n, out int id)) throw new ArgumentException($"loadout {e.Name}: unknown item {n}");
                            items.Add(id);
                        }
                        e.Items = items.ToArray();
                        if (o["features"] is JArray f) for (int i = 0; i < FeatureCount && i < f.Count; i++) e.Features[i] = (float)f[i];
                        t[e.Code] = e;
                    }
                }
                return table = t;
            }
        }

        public static Entry Get(int code) => Table.TryGetValue(code, out Entry e) ? e : Table[NavigateEnv.LoadoutTsunami];

        public static int JumpTiles(int code) => Get(code).JumpTiles;

        /// <summary>Wear exactly the loadout's items in accessory slots 3-7 (the rest empty).</summary>
        public static void Equip(Player p, int code) {
            int[] items = Get(code).Items;
            for (int i = 0; i < Slots; i++) {
                int want = i < items.Length ? items[i] : ItemID.None;
                if (p.armor[FirstSlot + i].type != want) p.armor[FirstSlot + i].SetDefaults(want);
            }
        }
    }
}
