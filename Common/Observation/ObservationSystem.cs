using System;
using System.Diagnostics;
using System.Linq;
using FirstMod_Sp.Common.Automation;
using FirstMod_Sp.Common.Systems;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using Terraria;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Observation
{
    /// <summary>
    /// Bridge commands for the observation:
    ///   observation_config {grid, visibility, light, entities}  change settings, reply with layout
    ///   observation_spec                                         layout, feature names, tile class table
    ///   observe {bot | player, target: {x, y}}                   build one observation (grids base64, vectors named)
    /// </summary>
    public class ObservationSystem : ModSystem
    {
        private static byte[] buffer = Array.Empty<byte>();

        public override void Load() {
            ObservationConfig.LoadFromLaunchArgs();
            if (!AutomationArgs.Enabled) return;
            BridgeCommands.Register("observation_config", Config);
            BridgeCommands.Register("observation_spec", r => r.Reply(Spec(includeClasses: true)));
            BridgeCommands.Register("observe", Observe);
        }

        public static object Layout() => new {
            grid = ObservationConfig.Grid.ToString(),
            visibility = ObservationConfig.Visibility.ToString(),
            light_radius = ObservationConfig.LightRadius,
            entities = ObservationConfig.Entities,
            waypoint = ObservationConfig.Waypoint,
            waypoint_los = ObservationConfig.WaypointLos,
            fine = new { w = ObservationConfig.FineW, h = ObservationConfig.FineH, bytes_per_cell = 4, offset = 0, bytes = ObservationBuilder.FineBytes },
            coarse = ObservationConfig.HasCoarse
                ? new { w = ObservationConfig.CoarseW, h = ObservationConfig.CoarseH, tiles_per_cell = ObservationConfig.CoarseCell, offset = ObservationBuilder.FineBytes, bytes = ObservationBuilder.CoarseBytes }
                : null,
            floats = new {
                offset = ObservationBuilder.FineBytes + ObservationBuilder.CoarseBytes,
                count = ObservationBuilder.FloatCount,
                self = ObservationBuilder.SelfNames.Length,
                capabilities = ObservationBuilder.CapabilityNames.Length,
                goal = ObservationBuilder.GoalNames.Length,
                entity_slots = ObservationConfig.EntitySlots,
                entity_features = ObservationBuilder.EntityFeatureNames.Length,
            },
            total_bytes = ObservationBuilder.TotalBytes,
        };

        public static object Spec(bool includeClasses) => new {
            layout = Layout(),
            fine_cell = new[] { "class", "shape (0 full, 1 half, 2-5 slopes)", "liquid (amount/4 | type << 6; 0 water 1 lava 2 honey 3 shimmer)", "flags (1 wall, 2 visible, 4 remembered, 8 locked door, 16 outside world)" },
            coarse_cell = new[] { "solid count", "platform count", "bits (1 water, 2 lava, 4 honey, 8 shimmer, 16 hazard, 32 rope)", "visible count" },
            self_names = ObservationBuilder.SelfNames,
            capability_names = ObservationBuilder.CapabilityNames,
            goal_names = ObservationBuilder.GoalNames,
            entity_feature_names = ObservationBuilder.EntityFeatureNames,
            class_feature_names = TileClasses.FeatureNames,
            classes = includeClasses ? TileClasses.Classes : null,
            class_count = TileClasses.Classes.Length,
        };

        private static void Config(BridgeRequest r) {
            foreach (var (key, value) in r.Args) ObservationConfig.Set(key, value.ToString());
            r.Reply(Layout());
        }

        private static void Observe(BridgeRequest r) {
            Player player = r.Args["bot"] != null
                ? Main.player[BotManagerSystem.ActiveBots[r.Args.Value<int>("bot")]]
                : Main.player[r.Args.Value<int?>("player") ?? (Main.dedServ ? BotManagerSystem.ActiveBots[0] : Main.myPlayer)];
            var op = player.GetModPlayer<ObservationPlayer>();
            if (r.Args["target"] is JObject t) op.Target = new Vector2(t.Value<float>("x") * 16f + 8f, t.Value<float>("y") * 16f + 8f);

            if (buffer.Length < ObservationBuilder.TotalBytes) buffer = new byte[ObservationBuilder.TotalBytes];
            var watch = Stopwatch.StartNew();
            int bytes = ObservationBuilder.Build(player, buffer);
            double buildUs = watch.Elapsed.TotalMilliseconds * 1000;

            int fine = ObservationBuilder.FineBytes, coarse = ObservationBuilder.CoarseBytes;
            float[] floats = new float[ObservationBuilder.FloatCount];
            Buffer.BlockCopy(buffer, fine + coarse, floats, 0, floats.Length * sizeof(float));
            int f = 0;
            JObject Named(string[] names) {
                var o = new JObject();
                foreach (string name in names) o[name] = Math.Round(floats[f++], 4);
                return o;
            }
            var self = Named(ObservationBuilder.SelfNames);
            var caps = Named(ObservationBuilder.CapabilityNames);
            var goal = Named(ObservationBuilder.GoalNames);
            int stride = ObservationBuilder.EntityFeatureNames.Length;
            var entities = Enumerable.Range(0, ObservationConfig.EntitySlots)
                .Where(s => floats[f + s * stride] > 0)
                .Select(s => ObservationBuilder.EntityFeatureNames.Select((name, i) => (name, value: floats[f + s * stride + i]))
                    .ToDictionary(p => p.name, p => Math.Round(p.value, 3)))
                .ToList();

            r.Reply(new {
                player = player.whoAmI,
                center_tile = new { x = (int)(player.Center.X / 16), y = (int)(player.Center.Y / 16) },
                build_us = Math.Round(buildUs, 1),
                phases_us = new { read = ObservationBuilder.LastPhaseUs[0], visibility = ObservationBuilder.LastPhaseUs[1], fine = ObservationBuilder.LastPhaseUs[2], coarse = ObservationBuilder.LastPhaseUs[3] },
                bytes,
                layout = Layout(),
                fine = Convert.ToBase64String(buffer, 0, fine),
                coarse = coarse > 0 ? Convert.ToBase64String(buffer, fine, coarse) : null,
                self,
                capabilities = caps,
                goal,
                entities,
            });
        }
    }
}
