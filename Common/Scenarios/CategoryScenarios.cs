using System;
using System.Collections.Generic;
using FirstMod_Sp.Common.Automation;
using FirstMod_Sp.Common.Systems;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Scenarios
{
    /// <summary>
    /// Scenarios that exercise one movement category on purpose (docs/Navigate Progress.md, Oct 20: of 14,400 random
    /// training scenarios none passed honey and under 2% water or platforms). The categories come from the game's
    /// movement mechanics, not from failures:
    ///   liquids:  water, honey (start in a pool, the target outside it; Player.cs: honeyWet and wet change speed and
    ///             jumps);
    ///   floors:   ice (TileID.Sets.IceSkateSlippery), honey_block (TileID.HoneyBlock, Player.sticky: run acceleration
    ///             x 0.25): the route walks at least 4 cells on them;
    ///   passages: web (cobwebs on the route), platform (the route drops or jumps through a platform), door (the route
    ///             passes a door);
    ///   shape:    switchback (the route turns back horizontally 2+ times: away, back, away again).
    /// Each attempt picks an anchor tile of the category, a target near it, and starts at the requested route distance
    /// whose planned route has the category. Bridge command:
    ///   generate_category_scenarios {category, count, min_distance, max_distance, seed, max_jump, max_air_gap}
    /// Rows carry the route's feature counts.
    /// </summary>
    public class CategoryScenarios : ModSystem
    {
        public static readonly string[] Categories = { "water", "honey", "ice", "honey_block", "web", "platform", "door", "switchback" };

        public override void Load() {
            if (AutomationArgs.Enabled) BridgeCommands.Register("generate_category_scenarios", Command);
        }

        public sealed class Row : IPlainData
        {
            public int start_x, start_y, target_x, target_y, path_distance;
            public float straight_distance;
            public int height_delta;
            public string layer, category;
            public bool start_in_liquid;
            public int water, honey, web, platform, door, ice, honey_block, reversals, max_climb, max_drop, steps;
        }

        private static void Command(BridgeRequest r) {
            var a = r.Args;
            string category = a.Value<string>("category") ?? "honey";
            if (Array.IndexOf(Categories, category) < 0) throw new ArgumentException("category must be one of " + string.Join(", ", Categories));
            var rows = Generate(category, a.Value<int?>("count") ?? 50, a.Value<int?>("min_distance") ?? 8, a.Value<int?>("max_distance") ?? 40,
                a.Value<int?>("seed") ?? 1, a.Value<int?>("max_jump") ?? 13, a.Value<int?>("max_air_gap") ?? 5, out int tries, out int anchors);
            r.Reply(new { world = Main.worldName, category, anchors, tries, count = rows.Count, rows });
        }

        private static bool IsLiquid(string c) => c == "water" || c == "honey";

        private static bool Anchor(string c, int x, int y) {
            Tile t = Main.tile[x, y];
            switch (c) {
                case "water": return t.LiquidAmount > 128 && t.LiquidType == LiquidID.Water;
                case "honey": return t.LiquidAmount > 128 && t.LiquidType == LiquidID.Honey;
                case "web": return t.HasUnactuatedTile && t.TileType == TileID.Cobweb;
                case "platform": return t.HasUnactuatedTile && TileID.Sets.Platforms[t.TileType];
                case "door": return t.HasUnactuatedTile && (t.TileType == TileID.ClosedDoor || t.TileType == TileID.OpenDoor
                    || t.TileType == TileID.TallGateClosed || t.TileType == TileID.TallGateOpen);
                case "ice": return t.HasUnactuatedTile && TileID.Sets.IceSkateSlippery[t.TileType] && !Main.tile[x, y - 1].HasUnactuatedTile;
                case "honey_block": return t.HasUnactuatedTile && t.TileType == TileID.HoneyBlock && !Main.tile[x, y - 1].HasUnactuatedTile;
                case "switchback": return true;  // any spot: the route decides
            }
            return false;
        }

        private static bool Matches(string c, Row f) => c switch {
            "water" => f.start_in_liquid || f.water >= 2,
            "honey" => f.start_in_liquid || f.honey >= 1,
            "ice" => f.ice >= 4,
            "honey_block" => f.honey_block >= 4,
            "web" => f.web >= 1,
            "platform" => f.platform >= 1,
            "door" => f.door >= 1,
            "switchback" => f.reversals >= 2,
            _ => false,
        };

        /// <summary>A start inside a liquid pool: the body fits, something supports it below, liquid at the body.</summary>
        private static bool LiquidStart(int x, int y, string c) {
            if (x < 50 || x >= Main.maxTilesX - 50 || y < 50 || y >= Main.maxTilesY - 50) return false;
            for (int j = 0; j < 3; j++) if (GridSensors.ClassifyPathTile(x, y - j) == PathTile.Solid) return false;
            if (GridSensors.ClassifyPathTile(x - 1, y) == PathTile.Solid && GridSensors.ClassifyPathTile(x + 1, y) == PathTile.Solid) return false;
            if (GridSensors.ClassifyPathTile(x, y + 1) == PathTile.Air) return false;
            Tile t = Main.tile[x, y];
            return t.LiquidAmount > 128 && t.LiquidType == (c == "honey" ? LiquidID.Honey : LiquidID.Water);
        }

        /// <summary>Walk the planned route from (x, y) and count what it passes.</summary>
        private static void Features(PathDistanceField field, int x, int y, Row f) {
            int climb = 0, drop = 0, lastDx = 0;
            for (int k = 0; k < 600; k++) {
                for (int j = 0; j < 3; j++) {
                    Tile b = Main.tile[x, y - j];
                    if (b.LiquidAmount > 32 && b.LiquidType == LiquidID.Water) { f.water++; break; }
                }
                for (int j = 0; j < 3; j++) {
                    Tile b = Main.tile[x, y - j];
                    if (b.LiquidAmount > 32 && b.LiquidType == LiquidID.Honey) { f.honey++; break; }
                }
                for (int j = 0; j < 3; j++) if (Main.tile[x, y - j].HasUnactuatedTile && Main.tile[x, y - j].TileType == TileID.Cobweb) { f.web++; break; }
                for (int j = 0; j < 3; j++) if (Anchor("door", x, y - j)) { f.door++; break; }
                Tile floor = Main.tile[x, y + 1];
                if (floor.HasUnactuatedTile && TileID.Sets.IceSkateSlippery[floor.TileType]) f.ice++;
                if (floor.HasUnactuatedTile && floor.TileType == TileID.HoneyBlock) f.honey_block++;
                if (!field.Waypoint(x, y, 1, out int nx, out int ny) || (nx == x && ny == y)) break;
                f.steps++;
                int dx = nx - x, dy = ny - y;
                // through a platform: dropping from a cell standing on one, or jumping up into a platform cell
                if (dy > 0 && GridSensors.ClassifyPathTile(x, y + 1) == PathTile.Platform || dy < 0 && GridSensors.ClassifyPathTile(nx, ny) == PathTile.Platform) f.platform++;
                if (dy < 0) { climb++; f.max_climb = Math.Max(f.max_climb, climb); } else climb = 0;
                if (dy > 0) { drop++; f.max_drop = Math.Max(f.max_drop, drop); } else drop = 0;
                if (dx != 0) { if (lastDx != 0 && dx != lastDx) f.reversals++; lastDx = dx; }
                x = nx;
                y = ny;
            }
        }

        private static string LayerOf(int y) => y < Main.worldSurface ? "surface" : y < Main.rockLayer ? "underground" : "cavern";

        public static List<Row> Generate(string c, int count, int minDistance, int maxDistance, int seed, int maxJump, int maxAirGap,
            out int tries, out int anchorCount) {
            var rng = new Random(seed);
            int top = 60, bottom = Main.UnderworldLayer - 20;
            var anchors = new List<(int x, int y)>();
            if (c != "switchback") {
                for (int x = 60; x < Main.maxTilesX - 60; x++)
                    for (int y = top; y < bottom; y++)
                        if (Anchor(c, x, y)) anchors.Add((x, y));
            }
            anchorCount = anchors.Count;
            var rows = new List<Row>();
            var seen = new HashSet<long>();
            tries = 0;
            if (c != "switchback" && anchors.Count == 0) return rows;
            int radius = Math.Max(maxDistance + 10, 40);
            while (rows.Count < count && tries < count * 60) {
                tries++;
                int ax, ay;
                if (c == "switchback") { ax = rng.Next(100, Main.maxTilesX - 100); ay = rng.Next(top, bottom); }
                else (ax, ay) = anchors[rng.Next(anchors.Count)];
                // target: a standable cell (dry) somewhere around the anchor, up to the band's distance away
                int tx = -1, ty = -1;
                for (int k = 0; k < 40 && tx < 0; k++) {
                    int cx = ax + rng.Next(-maxDistance / 2, maxDistance / 2 + 1);
                    int cy = ay + rng.Next(-maxDistance / 3, maxDistance / 3 + 1);
                    for (int dy = 0; dy < 12 && tx < 0; dy++) {
                        if (ScenarioGenerator.Standable(cx, cy + dy)) { tx = cx; ty = cy + dy; }
                    }
                }
                if (tx < 0) continue;
                var field = PathDistanceField.Build(GridSensors.ClassifyPathTile, tx + 0.5f, ty - 0.3f, radius, maxJump, 1.5f, maxAirGap,
                    GridSensors.PathInfo, Env.NavigateEnv.MaxSafeFallTiles, Env.NavigateEnv.ConsistentBody);
                // starts: for liquids, inside the anchor's pool; otherwise standable cells on the far side of the anchor
                for (int k = 0; k < 60 && rows.Count < count; k++) {
                    int sx, sy;
                    bool inLiquid = false;
                    if (IsLiquid(c)) {
                        sx = ax + rng.Next(-6, 7);
                        sy = ay + rng.Next(-3, 6);
                        if (!LiquidStart(sx, sy, c)) continue;
                        inLiquid = true;
                    } else {
                        sx = ax + rng.Next(-maxDistance, maxDistance + 1);
                        sy = ay + rng.Next(-maxDistance / 2, maxDistance / 2 + 1);
                        if (!ScenarioGenerator.Standable(sx, sy)) continue;
                    }
                    int d = field.GetDistance(sx, sy);
                    if (d < minDistance || d > maxDistance) continue;
                    long key = ((long)sx << 40) | ((long)sy << 26) | ((long)tx << 12) | (uint)(ty & 4095);
                    if (!seen.Add(key)) continue;
                    var f = new Row {
                        start_x = sx, start_y = sy, target_x = tx, target_y = ty, path_distance = d,
                        straight_distance = MathF.Round(MathF.Sqrt((sx - tx) * (sx - tx) + (sy - ty) * (sy - ty)), 1),
                        height_delta = sy - ty, layer = LayerOf(ty), category = c, start_in_liquid = inLiquid,
                    };
                    Features(field, sx, sy, f);
                    if (!Matches(c, f)) continue;
                    rows.Add(f);
                    break;  // one start per target: spread over the world
                }
            }
            return rows;
        }
    }
}
