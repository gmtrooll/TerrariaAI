using System;
using System.Collections.Generic;
using System.Linq;
using FirstMod_Sp.Common.Automation;
using Terraria;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Fidelity
{
    /// <summary>
    /// Switches that trade simulation fidelity for speed (method M2 in docs/Throughput and Automation Plan.md).
    /// Everything is on (vanilla) unless a launch argument or the bridge's "fidelity" command turns it off. Each
    /// switch uses the most native mechanism available, found in the decompiled source:
    ///   liquids        Liquid.UpdateLiquid (flow and settling, every 2nd tick in WorldGen.UpdateWorld_Inner).
    ///                  Standing water stays and swimming works; water just never moves.
    ///   world_updates  random tile updates: grass and plant growth, tree growth, corruption spread, town NPCs
    ///                  moving in, falling stars. Done through vanilla's own tile update rate
    ///                  (Main.desiredWorldTilesUpdateRate = 0, what Journey mode's tile growth slider does).
    ///   npc_spawns     natural enemy spawning (tModLoader's GlobalNPC.EditSpawnRate, maxSpawns = 0).
    ///   wiring         Wiring.UpdateMech: timers of mechanisms (doors, traps, timers).
    ///   events         clock, rain, slime rain and event timers (Main.dayRate and desiredWorldEventsUpdateRate = 0,
    ///                  what Journey mode's freeze time does).
    /// Which training methods may use which switch is decided per switch from measurements (speedup) and what
    /// the skill needs; see the plan's fidelity section.
    ///
    /// Launch: -fidelity movement   or   -fidelity liquids=0,npc_spawns=0
    /// </summary>
    public class FidelitySystem : ModSystem
    {
        public static readonly string[] Switches = { "liquids", "world_updates", "npc_spawns", "wiring", "events" };

        /// <summary>
        /// Named sets of switches turned off. Only switches measured to be worth it belong here (interleaved A/B,
        /// server, lean bots, Oct 17): world_updates saves ~0.12 ms/tick (+41% at 4 bots, +27% at 16, +19% at 64).
        /// liquids, npc_spawns, wiring and events were within the ±10% noise in a settled world, so they stay on
        /// everywhere: no speed to gain, fidelity to lose.
        /// </summary>
        public static readonly Dictionary<string, string[]> Profiles = new Dictionary<string, string[]> {
            ["full"] = Array.Empty<string>(),
            // Grass spread, plant and tree growth, town NPCs moving in and falling stars do not matter over a
            // movement episode (seconds of game time).
            ["movement"] = new[] { "world_updates" },
        };

        private static readonly HashSet<string> off = new HashSet<string>();

        public static bool On(string name) => !off.Contains(name);

        public static Dictionary<string, bool> State() => Switches.ToDictionary(s => s, On);

        public static void Set(string name, bool enabled) {
            if (!Switches.Contains(name)) {
                throw new ArgumentException($"unknown fidelity switch '{name}'; known: {string.Join(", ", Switches)}");
            }
            if (enabled) off.Remove(name);
            else off.Add(name);
        }

        public static void ApplyProfile(string profile) {
            if (!Profiles.TryGetValue(profile, out string[] disabled)) {
                throw new ArgumentException($"unknown fidelity profile '{profile}'; known: {string.Join(", ", Profiles.Keys)}");
            }
            off.Clear();
            foreach (string name in disabled) off.Add(name);
        }

        /// <summary>"movement", or "liquids=0,npc_spawns=0" (0/false = off, 1/true = on).</summary>
        public static void Apply(string spec) {
            if (!spec.Contains('=')) {
                ApplyProfile(spec);
                return;
            }
            foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                string[] kv = part.Split('=');
                Set(kv[0], kv.Length > 1 && (kv[1] == "1" || kv[1].Equals("true", StringComparison.OrdinalIgnoreCase)));
            }
        }

        public override void Load() {
            off.Clear();
            if (Program.LaunchParameters.TryGetValue("-fidelity", out string spec) && !string.IsNullOrWhiteSpace(spec)) Apply(spec);
            On_Liquid.UpdateLiquid += UpdateLiquid;
            On_Wiring.UpdateMech += UpdateMech;
            On_Main.UpdateTimeRate_Inner += UpdateTimeRate;
            if (AutomationArgs.Enabled) BridgeCommands.Register("fidelity", Command);
        }

        public override void Unload() {
            On_Liquid.UpdateLiquid -= UpdateLiquid;
            On_Wiring.UpdateMech -= UpdateMech;
            On_Main.UpdateTimeRate_Inner -= UpdateTimeRate;
            off.Clear();
        }

        /// <summary>fidelity {profile: "movement"} or {set: {liquids: false}}; replies with the current state.</summary>
        private static void Command(BridgeRequest r) {
            if (r.Args.Value<string>("profile") is string profile) ApplyProfile(profile);
            if (r.Args["set"] is Newtonsoft.Json.Linq.JObject set) {
                foreach (var (name, value) in set) Set(name, value.ToObject<bool>());
            }
            r.Reply(new { state = State(), profiles = Profiles.ToDictionary(p => p.Key, p => p.Value) });
        }

        private static void UpdateLiquid(On_Liquid.orig_UpdateLiquid orig) {
            if (On("liquids")) orig();
        }

        private static void UpdateMech(On_Wiring.orig_UpdateMech orig) {
            if (On("wiring")) orig();
        }

        // UpdateTimeRate_Inner recomputes the rates every time update, so override them after it runs.
        private static void UpdateTimeRate(On_Main.orig_UpdateTimeRate_Inner orig) {
            orig();
            if (Main.gameMenu) return;
            if (!On("world_updates")) Main.desiredWorldTilesUpdateRate = 0;
            if (!On("events")) {
                Main.dayRate = 0;
                Main.desiredWorldEventsUpdateRate = 0;
            }
        }
    }

    public class FidelitySpawnBlock : GlobalNPC
    {
        public override void EditSpawnRate(Player player, ref int spawnRate, ref int maxSpawns) {
            if (!FidelitySystem.On("npc_spawns")) maxSpawns = 0;
        }
    }
}
