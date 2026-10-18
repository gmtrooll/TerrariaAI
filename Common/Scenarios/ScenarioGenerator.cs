using System;
using System.Collections.Generic;
using System.Linq;
using FirstMod_Sp.Common.Automation;
using FirstMod_Sp.Common.Observation;
using FirstMod_Sp.Common.Systems;
using Terraria;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Scenarios
{
    /// <summary>
    /// Generates Navigate scenarios (start and target pairs) in the loaded world, replacing the hand-placed zones of
    /// ZoneMarkerTool. For each sampled target it builds the planner's PathDistanceField (reverse BFS with the jump
    /// limit) and picks starts at the requested path distances, so every pair is reachable under that movement
    /// model and the curriculum can be ordered by path distance.
    ///
    /// Positions are feet cells: the tile the player's feet occupy, with solid ground or a platform below and room
    /// for the 3-tile-tall body above. Bridge command:
    ///   generate_scenarios {count, min_distance, max_distance, layer, seed, max_jump, max_air_gap, radius, per_target, with_path}
    /// </summary>
    public class ScenarioGenerator : ModSystem
    {
        public override void Load() {
            if (AutomationArgs.Enabled) BridgeCommands.Register("generate_scenarios", Command);
        }

        public sealed class Scenario : IPlainData
        {
            public int start_x, start_y, target_x, target_y;
            public int path_distance;
            public float straight_distance;
            public int height_delta;  // start_y - target_y (positive: target is higher)
            public string layer;
            public List<int[]> path;
        }

        private static void Command(BridgeRequest r) {
            var a = r.Args;
            var scenarios = Generate(
                count: a.Value<int?>("count") ?? 100,
                minDistance: a.Value<int?>("min_distance") ?? 10,
                maxDistance: a.Value<int?>("max_distance") ?? 60,
                layer: a.Value<string>("layer") ?? "surface",
                seed: a.Value<int?>("seed") ?? 1,
                maxJump: a.Value<int?>("max_jump") ?? 13,  // measured: 6.1 tiles single jump, 13.1 with the bots' Tsunami in a Bottle
                maxAirGap: a.Value<int?>("max_air_gap") ?? 5,
                radius: a.Value<int?>("radius") ?? 120,
                perTarget: a.Value<int?>("per_target") ?? 3,
                withPath: a.Value<bool?>("with_path") ?? false,
                out int targetsTried);
            r.Reply(new { world = Main.worldName, seed = Main.ActiveWorldFileData?.SeedText, targets_tried = targetsTried, count = scenarios.Count, scenarios });
        }

        /// <summary>Feet cell (x, y) can hold a standing player: the cell and two above are free of solid tiles, the
        /// tile below supports, no liquid at the feet, and no contact-damage tile within one tile.</summary>
        public static bool Standable(int x, int y) {
            if (x < 50 || x >= Main.maxTilesX - 50 || y < 50 || y >= Main.maxTilesY - 50) return false;
            // The body is 3 tiles tall and 2 wide (20 px): this column and one neighbour must be clear.
            bool Clear(int cx) {
                for (int j = 0; j < 3; j++) {
                    if (GridSensors.ClassifyPathTile(cx, y - j) == PathTile.Solid) return false;
                }
                return true;
            }
            if (!Clear(x) || (!Clear(x - 1) && !Clear(x + 1))) return false;
            if (GridSensors.ClassifyPathTile(x, y + 1) == PathTile.Air) return false;
            if (Main.tile[x, y].LiquidAmount > 0) return false;
            for (int i = -1; i <= 1; i++) {
                for (int j = -3; j <= 1; j++) {
                    Tile t = Main.tile[x + i, y + j];
                    if (t.HasUnactuatedTile && TileClasses.Hazard[t.TileType]) return false;
                    // Not in a doorway: the planner treats closed doors as open, but a bot placed inside one is stuck.
                    if (t.HasTile && (t.TileType == Terraria.ID.TileID.ClosedDoor || t.TileType == Terraria.ID.TileID.TallGateClosed)) return false;
                }
            }
            return true;
        }

        private static (int top, int bottom) LayerRows(string layer) {
            int surface = (int)Main.worldSurface, rock = (int)Main.rockLayer, hell = Main.UnderworldLayer;
            return layer switch {
                "surface" => (60, surface),
                "underground" => (surface, rock),
                "cavern" => (rock, hell - 20),
                "any" => (60, hell - 20),
                _ => throw new ArgumentException("layer must be surface, underground, cavern or any"),
            };
        }

        public static List<Scenario> Generate(int count, int minDistance, int maxDistance, string layer, int seed,
            int maxJump, int maxAirGap, int radius, int perTarget, bool withPath, out int targetsTried) {
            var rng = new Random(seed);
            var (top, bottom) = LayerRows(layer);
            var result = new List<Scenario>();
            targetsTried = 0;
            int maxTargets = count * 20;
            var column = new List<int>();
            while (result.Count < count && targetsTried < maxTargets) {
                targetsTried++;
                int tx = rng.Next(100, Main.maxTilesX - 100);
                column.Clear();
                for (int y = top; y < bottom; y++) {
                    if (Standable(tx, y)) column.Add(y);
                }
                if (column.Count == 0) continue;
                int ty = column[rng.Next(column.Count)];

                // Target point = body centre of a player standing on the target cell (as in PathDistanceField's goals).
                var field = PathDistanceField.Build(GridSensors.ClassifyPathTile, tx + 0.5f, ty - 0.3f, radius, maxJump, 1.5f, maxAirGap,
                    GridSensors.PathInfo, Env.NavigateEnv.MaxSafeFallTiles);
                var byDistance = new List<(int x, int y, int d)>();
                for (int y = ty - radius; y <= ty + radius; y++) {
                    for (int x = tx - radius; x <= tx + radius; x++) {
                        int d = field.GetDistance(x, y);
                        if (d >= minDistance && d <= maxDistance && Standable(x, y)) byDistance.Add((x, y, d));
                    }
                }
                if (byDistance.Count == 0) continue;

                // Spread the picks over the distance range: choose a distance uniformly, then a cell near it.
                for (int p = 0; p < perTarget && result.Count < count; p++) {
                    int want = rng.Next(minDistance, maxDistance + 1);
                    var pool = byDistance.Where(c => Math.Abs(c.d - want) <= Math.Max(2, (maxDistance - minDistance) / 10)).ToList();
                    if (pool.Count == 0) pool = byDistance;
                    var (sx, sy, sd) = pool[rng.Next(pool.Count)];
                    result.Add(new Scenario {
                        start_x = sx, start_y = sy, target_x = tx, target_y = ty,
                        path_distance = sd,
                        straight_distance = MathF.Round(MathF.Sqrt((sx - tx) * (sx - tx) + (sy - ty) * (sy - ty)), 1),
                        height_delta = sy - ty,
                        layer = layer,
                        path = withPath ? TracePath(field, sx, sy) : null,
                    });
                }
            }
            return result;
        }

        /// <summary>Follow the planned route from the start to a goal cell (for drawing and checking).</summary>
        private static List<int[]> TracePath(PathDistanceField field, int x, int y) {
            var path = new List<int[]> { new[] { x, y } };
            while (path.Count < 2000 && field.Waypoint(x, y, 1, out int nx, out int ny) && (nx != x || ny != y)) {
                x = nx;
                y = ny;
                path.Add(new[] { x, y });
            }
            return path;
        }
    }
}
