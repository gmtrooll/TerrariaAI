using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;

namespace FirstMod_Sp.Common.Observation
{
    /// <summary>
    /// Builds one bot's observation (docs/Observation Design.md) into a byte buffer:
    ///   [fine grid FineW x FineH x 4 bytes][coarse grid 30 x 17 x 4 bytes, Fovea only][float32 vectors]
    /// Fine cell bytes: 0 tile class (TileClasses), 1 shape (BlockType 0-5), 2 liquid (amount/4 | type << 6),
    ///   3 flags (bit 0 wall, 1 visible, 2 remembered, 3 locked door, 4 outside the world).
    /// Coarse cell bytes (4x4 tiles, visible tiles only): 0 solid count, 1 platform count,
    ///   2 bits (0 water, 1 lava, 2 honey, 3 shimmer, 4 hazard, 5 rope), 3 visible count.
    /// Grids are row-major (y outer, x inner), top-left first. Vectors follow in the order of the *Names arrays.
    ///
    /// The area around the bot is read from Main.tile once per build into scratch arrays; visibility and both grids
    /// are computed from those. Not thread-safe (the game is single-threaded).
    /// </summary>
    public static class ObservationBuilder
    {
        public static readonly string[] SelfNames = {
            "vx", "vy", "facing", "feet_x_in_tile", "feet_y_in_tile", "grounded", "grav_dir",
            "jump_left", "can_jump", "extra_jumps_available", "extra_jump_active",
            "wing_time", "rocket_time", "carpet_time",
            "wall_sliding", "slide_dir", "on_rope", "hooks_latched",
            "dash_delay", "dash_tap_window", "fall_margin",
            "in_water", "in_lava", "in_honey", "in_shimmer", "breath", "lava_immunity_left",
            "floor_slippery", "floor_sticky", "floor_asphalt",
            "life", "immune_time", "crowd_controlled", "confused", "tongued",
            "last_left", "last_right", "last_up", "last_down", "last_jump", "dead",
        };

        public static readonly string[] CapabilityNames = {
            "max_run_speed", "sprint_speed", "run_acceleration", "run_slowdown",
            "jump_duration", "jump_speed", "gravity", "max_fall_speed",
            "wing_time_max", "rocket_boots", "dash", "carpet", "wall_slide", "auto_jump",
            "no_fall_damage", "extra_fall", "water_walk", "lava_immune", "lava_immunity_max", "breathes_underwater",
            "slow_fall", "life_max", "defense", "no_knockback", "height", "gravity_control", "ice_skates",
        };

        public static readonly string[] GoalNames = { "has_target", "dx", "dy", "dir_x", "dir_y", "log_distance",
            "has_waypoint", "wp_dx", "wp_dy", "wp_dir_x", "wp_dir_y" };

        public static readonly string[] EntityFeatureNames = {
            "present", "npc", "projectile", "item", "dx", "dy", "vx", "vy", "width", "height",
            "life", "damage", "hostile", "boss",
        };

        /// <summary>How far sky light spreads from open columns through air, in tiles.</summary>
        public const int SkyFloodTiles = 12;

        public const int GoalClipTiles = 20;
        public const float GoalLogNorm = 6.9f;  // log1p(1000 tiles)

        public static int FineBytes => ObservationConfig.FineW * ObservationConfig.FineH * 4;
        public static int CoarseBytes => ObservationConfig.HasCoarse ? ObservationConfig.CoarseW * ObservationConfig.CoarseH * 4 : 0;
        public static int FloatCount => SelfNames.Length + CapabilityNames.Length + GoalNames.Length + ObservationConfig.EntitySlots * EntityFeatureNames.Length;
        public static int TotalBytes => FineBytes + CoarseBytes + FloatCount * sizeof(float);

        // ---------- scratch area (region around the bot, with a 1-tile margin) ----------
        private static int rx0, ry0, rw, rh;
        private static ushort[] type = Array.Empty<ushort>();
        private static byte[] block = Array.Empty<byte>();   // 1 = unactuated block present
        private static byte[] solid = Array.Empty<byte>();   // 1 = blocks light and movement (solid, not platform), or outside the world
        private static byte[] shape = Array.Empty<byte>();
        private static byte[] liquid = Array.Empty<byte>();
        private static byte[] flags = Array.Empty<byte>();
        private static byte[] visible = Array.Empty<byte>();
        private static byte[] lit = Array.Empty<byte>();     // bit 0 sky light, bit 1 the bot's light
        private static int[] queue = Array.Empty<int>();
        private static readonly int[] hooksByOwner = new int[256];
        private static uint hooksForTick = uint.MaxValue;

        /// <summary>Build the observation of `player` into `dest` (TotalBytes long). Returns bytes written.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int Build(Player player, Span<byte> dest) {
            if (dest.Length < TotalBytes) throw new ArgumentException($"buffer of {dest.Length} bytes, need {TotalBytes}");
            int cx = (int)(player.Center.X / 16f), cy = (int)(player.Center.Y / 16f);
            int fineW = ObservationConfig.FineW, fineH = ObservationConfig.FineH;
            int fx0 = cx - fineW / 2, fy0 = cy - fineH / 2;
            int vx0 = cx - ObservationConfig.ViewW / 2, vy0 = cy - ObservationConfig.ViewH / 2;

            var op = player.GetModPlayer<ObservationPlayer>();
            int floatOffset = FineBytes + CoarseBytes;
            Span<float> floats = MemoryMarshal.Cast<byte, float>(dest.Slice(floatOffset, FloatCount * sizeof(float)));
            floats.Clear();

            // Fine grid: its own small region, every decision. With many bots each reading a different part of
            // the world, the cost is dominated by cache misses per column and per game array read, so regions are
            // kept tight and the overview below reads fewer arrays, less often.
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ReadRegion(fx0 - 1, fy0 - 1, fineW + 2, fineH + 2, details: true);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            ComputeVisibility(player, cx, cy);
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            WriteFine(dest.Slice(0, FineBytes), fx0, fy0, fineW, fineH);
            if (ObservationConfig.Entities) WriteEntities(floats.Slice(SelfNames.Length + CapabilityNames.Length + GoalNames.Length), player, vx0, vy0, op);
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();

            // Overview: refreshed every CoarseEvery decisions per bot (distant terrain changes slowly), cached in between,
            // and at once when the bot moved more than one overview cell since (teleports, respawns, episode resets).
            if (ObservationConfig.HasCoarse) {
                bool moved = Math.Max(Math.Abs(cx - op.CoarseCenterX), Math.Abs(cy - op.CoarseCenterY)) > ObservationConfig.CoarseCell;
                if (op.CoarseCache == null || op.CoarseCache.Length != CoarseBytes || moved || op.CoarseVersion != ObservationConfig.Version || ++op.CoarseAge >= ObservationConfig.CoarseEvery) {
                    op.CoarseVersion = ObservationConfig.Version;
                    op.CoarseCenterX = cx;
                    op.CoarseCenterY = cy;
                    op.CoarseCache ??= new byte[CoarseBytes];
                    if (op.CoarseCache.Length != CoarseBytes) op.CoarseCache = new byte[CoarseBytes];
                    ReadRegion(vx0 - 1, vy0 - 1, ObservationConfig.ViewW + 2, ObservationConfig.ViewH + 2, details: false);
                    ComputeVisibility(player, cx, cy);
                    WriteCoarse(op.CoarseCache, vx0, vy0);
                    op.CoarseAge = 0;
                }
                op.CoarseCache.CopyTo(dest.Slice(FineBytes, CoarseBytes));
            }
            long t4 = System.Diagnostics.Stopwatch.GetTimestamp();
            LastPhaseUs = new[] { Us(t0, t1), Us(t1, t2), Us(t2, t3), Us(t3, t4) };

            int f = 0;
            f = WriteSelf(floats, f, player, op);
            f = WriteCapabilities(floats, f, player, op);
            WriteGoal(floats, f, player, op);
            return floatOffset + FloatCount * sizeof(float);
        }

        /// <summary>Microseconds of the last build per phase: read tiles, visibility, fine grid, coarse grid.</summary>
        public static double[] LastPhaseUs = new double[4];

        private static double Us(long from, long to) => Math.Round((to - from) * 1e6 / System.Diagnostics.Stopwatch.Frequency, 1);

        // ---------- tiles ----------

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ReadRegion(int x0, int y0, int w, int h, bool details) {
            rx0 = x0;
            ry0 = y0;
            rw = w;
            rh = h;
            int n = w * h;
            if (type.Length < n) {
                type = new ushort[n];
                block = new byte[n];
                solid = new byte[n];
                shape = new byte[n];
                liquid = new byte[n];
                flags = new byte[n];
                visible = new byte[n];
                lit = new byte[n];
                queue = new int[n];
            }
            // Read the game's raw per-tile arrays directly (Tilemap.GetData; index y + x * Height, column-major).
            // Going through Main.tile[x, y] and the Tile properties cost ~24 ns per tile.
            TileTypeData[] types = Main.tile.GetData<TileTypeData>();
            WallTypeData[] walls = Main.tile.GetData<WallTypeData>();
            LiquidData[] liquids = Main.tile.GetData<LiquidData>();
            TileWallWireStateData[] states = Main.tile.GetData<TileWallWireStateData>();
            int worldW = Main.maxTilesX, worldH = Main.maxTilesY, height = Main.tile.Height;
            int jIn0 = Math.Max(0, -y0), jIn1 = Math.Min(h, worldH - y0);  // rows inside the world
            for (int i = 0; i < w; i++) {
                int x = x0 + i;
                bool columnInside = x >= 0 && x < worldW;
                for (int j = 0; j < h; j++) {
                    int k = j * w + i;
                    if (!columnInside || j < jIn0 || j >= jIn1) {
                        type[k] = 0;
                        block[k] = 0;
                        solid[k] = 1;
                        shape[k] = 0;
                        liquid[k] = 0;
                        flags[k] = 1 << 4;
                        continue;
                    }
                    int y = y0 + j;
                    int idx = y + x * height;
                    ref TileWallWireStateData state = ref states[idx];
                    ushort t = types[idx].Type;
                    bool hasBlock = state.HasTile && !state.IsActuated;
                    type[k] = t;
                    block[k] = hasBlock ? (byte)1 : (byte)0;
                    solid[k] = hasBlock && TileClasses.Solid[t] ? (byte)1 : (byte)0;
                    shape[k] = hasBlock ? (byte)state.BlockType : (byte)0;
                    ref LiquidData liq = ref liquids[idx];
                    liquid[k] = liq.Amount == 0 ? (byte)0 : (byte)((liq.Amount >> 2) | (liq.LiquidType << 6));
                    byte flag = 0;
                    if (details) {
                        if (walls[idx].Type != 0) flag = 1;
                        if (hasBlock && t == TileID.ClosedDoor && WorldGen.IsLockedDoor(x, y)) flag |= 1 << 3;
                    }
                    flags[k] = flag;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ComputeVisibility(Player player, int cx, int cy) {
            int n = rw * rh;
            if (ObservationConfig.Visibility == VisibilityMode.All) {
                Array.Fill(visible, (byte)1, 0, n);
                return;
            }

            // Light floods through non-solid tiles (light passes air ~0.91 per tile but only ~0.56 per solid tile,
            // LightMap.Blur; through rock it is gone within a few tiles). Two floods, each a plain breadth-first
            // search by levels:
            //  - sky: open tiles above Main.worldSurface with no solid tile above them in the region (sky light
            //    comes down open columns, TileLightScanner), spreading SkyFloodTiles further through air;
            //  - the bot itself, as if carrying a torch, spreading LightRadius tiles.
            // A tile is visible if light reached it, or if it is solid next to a lit tile (its exposed face).
            // The region's 1-tile margin is made solid so neighbours never need bounds checks; it is never output.
            Array.Clear(lit, 0, n);
            for (int i = 0; i < rw; i++) {
                solid[i] = 1;
                solid[(rh - 1) * rw + i] = 1;
            }
            for (int j = 0; j < rh; j++) {
                solid[j * rw] = 1;
                solid[j * rw + rw - 1] = 1;
            }

            int tail = 0;
            int surfaceRow = (int)Main.worldSurface - ry0;
            for (int i = 1; i < rw - 1; i++) {
                for (int j = 1; j < rh - 1 && j < surfaceRow; j++) {
                    int k = j * rw + i;
                    if (solid[k] != 0) break;
                    lit[k] = 1;
                }
            }
            // Flood only from column-lit tiles next to an open tile the columns did not reach (under overhangs,
            // into caves); everything else in the open sky is already lit.
            for (int i = 1; i < rw - 1; i++) {
                for (int j = 1; j < rh - 1; j++) {
                    int k = j * rw + i;
                    if (lit[k] == 0) break;
                    if ((solid[k - 1] == 0 && lit[k - 1] == 0) || (solid[k + 1] == 0 && lit[k + 1] == 0) || (solid[k + rw] == 0 && lit[k + rw] == 0)) {
                        queue[tail++] = k;
                    }
                }
            }
            Flood(tail, 1, SkyFloodTiles);

            int start = (cy - ry0) * rw + (cx - rx0);
            int radius = ObservationConfig.LightRadius;
            if (radius > 0 && start > rw && start < n - rw && solid[start] == 0) {
                lit[start] |= 2;
                queue[0] = start;
                Flood(1, 2, radius);
            }

            for (int j = 1; j < rh - 1; j++) {
                int row = j * rw;
                for (int i = 1; i < rw - 1; i++) {
                    int k = row + i;
                    bool vis = solid[k] == 0 ? lit[k] != 0 : (lit[k - 1] | lit[k + 1] | lit[k - rw] | lit[k + rw]) != 0;
                    visible[k] = vis ? (byte)1 : (byte)0;
                }
            }

            if (ObservationConfig.Visibility == VisibilityMode.ExposedMemory) ApplyMemory(player);
        }

        /// <summary>Breadth-first flood from the `seeds` queued at queue[0..seeds), marking `bit` in lit, `levels` steps.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Flood(int seeds, byte bit, int levels) {
            int head = 0, tail = seeds, levelEnd = seeds, w = rw;
            for (int level = 0; level < levels && head < tail; level++) {
                while (head < levelEnd) {
                    int k = queue[head++];
                    Try(k - 1);
                    Try(k + 1);
                    Try(k - w);
                    Try(k + w);
                }
                levelEnd = tail;
            }

            void Try(int k) {
                if (solid[k] != 0 || (lit[k] & bit) != 0) return;
                lit[k] |= bit;
                queue[tail++] = k;
            }
        }

        /// <summary>Mode d: record what is visible now; tiles seen before get visible = 2 (remembered).
        /// One bit per world tile, index y + x * maxTilesY.</summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void ApplyMemory(Player player) {
            var op = player.GetModPlayer<ObservationPlayer>();
            int worldH = Main.maxTilesY, words = (Main.maxTilesX * worldH + 63) / 64;
            if (op.SeenBits == null || op.SeenBits.Length != words) op.SeenBits = new ulong[words];
            ulong[] seen = op.SeenBits;
            int i0 = Math.Max(1, -rx0), i1 = Math.Min(rw - 1, Main.maxTilesX - rx0);
            int j0 = Math.Max(1, -ry0), j1 = Math.Min(rh - 1, worldH - ry0);
            for (int i = i0; i < i1; i++) {
                int column = (rx0 + i) * worldH + ry0;
                for (int j = j0; j < j1; j++) {
                    int k = j * rw + i, bit = column + j;
                    ulong mask = 1UL << (bit & 63);
                    if (visible[k] == 1) seen[bit >> 6] |= mask;
                    else if ((seen[bit >> 6] & mask) != 0) visible[k] = 2;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void WriteFine(Span<byte> dest, int fx0, int fy0, int w, int h) {
            int o = 0;
            for (int j = 0; j < h; j++) {
                int row = (fy0 + j - ry0) * rw + (fx0 - rx0);
                for (int i = 0; i < w; i++) {
                    int k = row + i;
                    byte v = visible[k];
                    byte flag = (byte)(flags[k] & 0b11001);  // wall, locked door, outside
                    if (v == 0) {
                        dest[o] = 0;
                        dest[o + 1] = 0;
                        dest[o + 2] = 0;
                        dest[o + 3] = (byte)(flag & 0b10000);  // only "outside the world" stays known
                    }
                    else {
                        dest[o] = block[k] != 0 ? TileClasses.ClassOf[type[k]] : (byte)0;
                        dest[o + 1] = shape[k];
                        dest[o + 2] = liquid[k];
                        dest[o + 3] = (byte)(flag | (v == 1 ? 1 << 1 : 1 << 2));
                    }
                    o += 4;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void WriteCoarse(Span<byte> dest, int vx0, int vy0) {
            int cell = ObservationConfig.CoarseCell, o = 0;
            for (int cj = 0; cj < ObservationConfig.CoarseH; cj++) {
                for (int ci = 0; ci < ObservationConfig.CoarseW; ci++) {
                    int solidCount = 0, platformCount = 0, visibleCount = 0, bits = 0;
                    for (int dj = 0; dj < cell; dj++) {
                        int row = (vy0 + cj * cell + dj - ry0) * rw + (vx0 + ci * cell - rx0);
                        for (int di = 0; di < cell; di++) {
                            int k = row + di;
                            if (visible[k] == 0) continue;
                            visibleCount++;
                            if (liquid[k] != 0) bits |= 1 << (liquid[k] >> 6);
                            if (block[k] == 0) continue;
                            ushort t = type[k];
                            if (TileClasses.Solid[t]) solidCount++;
                            if (TileClasses.Platform[t]) platformCount++;
                            if (TileClasses.Hazard[t]) bits |= 1 << 4;
                            if (TileClasses.Rope[t]) bits |= 1 << 5;
                        }
                    }
                    dest[o] = (byte)solidCount;
                    dest[o + 1] = (byte)platformCount;
                    dest[o + 2] = (byte)bits;
                    dest[o + 3] = (byte)visibleCount;
                    o += 4;
                }
            }
        }

        // ---------- vectors ----------

        private static float Clip(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        private static float B(bool v) => v ? 1f : 0f;

        private static int WriteSelf(Span<float> o, int f, Player p, ObservationPlayer op) {
            bool grounded = p.velocity.Y == 0f || p.sliding;
            int extraAvailable = 0;
            bool extraActive = false;
            foreach (ExtraJumpState state in p.ExtraJumps) {
                if (state.Enabled && state.Available) extraAvailable++;
                extraActive |= state.Active;
            }
            float fallTiles = (p.position.Y / 16f - p.fallStart) * p.gravDir;
            float safeFall = p.noFallDmg || p.wingsLogic > 0 ? 0f : 25 + p.extraFall;

            o[f++] = Clip(p.velocity.X / 10f, -2, 2);
            o[f++] = Clip(p.velocity.Y / 10f, -2, 2);
            o[f++] = p.direction;
            o[f++] = (p.position.X % 16f) / 16f;
            o[f++] = ((p.position.Y + p.height) % 16f) / 16f;
            o[f++] = B(grounded);
            o[f++] = p.gravDir;
            o[f++] = op.JumpHeight > 0 ? Clip(p.jump / (float)op.JumpHeight, 0, 1) : 0;
            o[f++] = B((grounded || p.AnyExtraJumpUsable()) && (p.releaseJump || (p.autoJump && p.velocity.Y == 0f)));
            o[f++] = Math.Min(extraAvailable, 4) / 4f;
            o[f++] = B(extraActive);
            o[f++] = p.wingTimeMax > 0 ? Clip(p.wingTime / p.wingTimeMax, 0, 1) : 0;
            o[f++] = p.rocketBoots > 0 && p.rocketTimeMax > 0 ? Clip(p.rocketTime / (float)p.rocketTimeMax, 0, 1) : 0;
            o[f++] = p.carpet ? Clip(p.carpetTime / 300f, 0, 1) : 0;
            o[f++] = B(p.sliding);
            o[f++] = p.slideDir;
            o[f++] = B(p.pulley);
            o[f++] = Math.Min(HooksLatched(p.whoAmI), 3) / 3f;
            o[f++] = Clip(p.dashDelay / 30f, -1, 1);
            o[f++] = Clip(p.dashTime / 15f, -1, 1);
            o[f++] = safeFall > 0 ? Clip(fallTiles / safeFall, -1, 2) : 0;
            o[f++] = B(p.wet && !p.lavaWet && !p.honeyWet && !p.shimmerWet);
            o[f++] = B(p.lavaWet);
            o[f++] = B(p.honeyWet);
            o[f++] = B(p.shimmerWet);
            o[f++] = p.breathMax > 0 ? Clip(p.breath / (float)p.breathMax, 0, 1) : 1;
            o[f++] = p.lavaMax > 0 ? Clip(p.lavaTime / (float)p.lavaMax, 0, 1) : 0;
            o[f++] = B(p.slippy || p.slippy2);
            o[f++] = B(p.sticky);
            o[f++] = B(p.powerrun);
            o[f++] = Clip(p.statLife / (float)Math.Max(1, p.statLifeMax2), 0, 1);
            o[f++] = Clip(p.immuneTime / 80f, 0, 1);
            o[f++] = B(p.CCed);
            o[f++] = B(p.confused);
            o[f++] = B(p.tongued);
            o[f++] = B(op.LastLeft);
            o[f++] = B(op.LastRight);
            o[f++] = B(op.LastUp);
            o[f++] = B(op.LastDown);
            o[f++] = B(op.LastJump);
            o[f++] = B(p.dead);
            return f;
        }

        /// <summary>Movement values as the game computed them this tick (gear, buffs, floor and liquid included).</summary>
        private static int WriteCapabilities(Span<float> o, int f, Player p, ObservationPlayer op) {
            o[f++] = p.maxRunSpeed / 10f;
            o[f++] = p.accRunSpeed / 10f;
            o[f++] = p.runAcceleration / 0.5f;
            o[f++] = p.runSlowdown / 0.5f;
            o[f++] = op.JumpHeight / 30f;
            o[f++] = op.JumpSpeed / 10f;
            o[f++] = op.Gravity / 0.5f;
            o[f++] = op.MaxFallSpeed / 20f;
            o[f++] = p.wingTimeMax / 200f;
            o[f++] = B(p.rocketBoots > 0);
            o[f++] = B(p.dashType > 0);
            o[f++] = B(p.carpet);
            o[f++] = B(p.spikedBoots > 0);
            o[f++] = B(p.autoJump);
            o[f++] = B(p.noFallDmg || p.wingsLogic > 0);
            o[f++] = p.extraFall / 30f;
            o[f++] = B(p.waterWalk || p.waterWalk2);
            o[f++] = B(p.lavaImmune);
            o[f++] = p.lavaMax / 1260f;
            o[f++] = B(p.gills || p.accMerman);
            o[f++] = B(p.slowFall);
            o[f++] = p.statLifeMax2 / 500f;
            o[f++] = (int)p.statDefense / 100f;
            o[f++] = B(p.noKnockback);
            o[f++] = p.height / 42f;
            o[f++] = B(p.gravControl || p.gravControl2);
            o[f++] = B(p.iceSkate);
            return f;
        }

        private static int WriteGoal(Span<float> o, int f, Player p, ObservationPlayer op) {
            int end = f + GoalNames.Length;
            if (op.Target is not Vector2 target) return end;
            Vector2 delta = (target - p.Center) / 16f;
            float distance = delta.Length();
            o[f++] = 1;
            o[f++] = Clip(delta.X, -GoalClipTiles, GoalClipTiles) / GoalClipTiles;
            o[f++] = Clip(delta.Y, -GoalClipTiles, GoalClipTiles) / GoalClipTiles;
            o[f++] = distance > 1e-4f ? delta.X / distance : 0;
            o[f++] = distance > 1e-4f ? delta.Y / distance : 0;
            o[f++] = Clip(MathF.Log(1 + distance) / GoalLogNorm, 0, 1);
            if (op.Waypoint is Vector2 waypoint) {
                Vector2 w = (waypoint - p.Center) / 16f;
                float wl = w.Length();
                o[f++] = 1;
                o[f++] = Clip(w.X, -GoalClipTiles, GoalClipTiles) / GoalClipTiles;
                o[f++] = Clip(w.Y, -GoalClipTiles, GoalClipTiles) / GoalClipTiles;
                o[f++] = wl > 1e-4f ? w.X / wl : 0;
                o[f++] = wl > 1e-4f ? w.Y / wl : 0;
            }
            return end;
        }

        /// <summary>Hooks are counted from projectiles (latched hooks: aiStyle 7, ai[0] == 2); Player.grapCount is
        /// already cleared when the player update ends (Player.cs:27079). One pass per tick for all bots.</summary>
        private static int HooksLatched(int owner) {
            if (hooksForTick != Main.GameUpdateCount) {
                hooksForTick = Main.GameUpdateCount;
                Array.Clear(hooksByOwner);
                foreach (Projectile proj in Main.projectile) {
                    if (proj.active && proj.aiStyle == 7 && proj.ai[0] == 2f && proj.owner >= 0 && proj.owner < 256) hooksByOwner[proj.owner]++;
                }
            }
            return hooksByOwner[owner];
        }

        // ---------- entities ----------

        private static readonly List<(float dist, Entity entity)> candidates = new List<(float, Entity)>();

        private static void WriteEntities(Span<float> o, Player p, int vx0, int vy0, ObservationPlayer op) {
            candidates.Clear();
            var view = new Rectangle(vx0 * 16, vy0 * 16, ObservationConfig.ViewW * 16, ObservationConfig.ViewH * 16);
            void Consider(Entity e) {
                if (!e.active || !view.Contains(e.Center.ToPoint())) return;
                int tx = (int)(e.Center.X / 16), ty = (int)(e.Center.Y / 16);
                int i = tx - rx0, j = ty - ry0;
                if (i > 0 && i < rw - 1 && j > 0 && j < rh - 1) {
                    if (visible[j * rw + i] != 1) return;
                }
                else if (op.CoarseCache != null) {  // outside the fine grid: any tile of its overview cell visible
                    int ci = (tx - vx0) / ObservationConfig.CoarseCell, cj = (ty - vy0) / ObservationConfig.CoarseCell;
                    if (ci < 0 || ci >= ObservationConfig.CoarseW || cj < 0 || cj >= ObservationConfig.CoarseH) return;
                    if (op.CoarseCache[(cj * ObservationConfig.CoarseW + ci) * 4 + 3] == 0) return;
                }
                candidates.Add((Vector2.DistanceSquared(e.Center, p.Center), e));
            }
            foreach (NPC npc in Main.npc) Consider(npc);
            foreach (Projectile proj in Main.projectile) {
                if (proj.owner != p.whoAmI) Consider(proj);
            }
            foreach (Item item in Main.item) Consider(item);
            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));

            int stride = EntityFeatureNames.Length;
            for (int s = 0; s < Math.Min(candidates.Count, ObservationConfig.EntitySlots); s++) {
                Entity e = candidates[s].entity;
                Span<float> slot = o.Slice(s * stride, stride);
                Vector2 delta = (e.Center - p.Center) / 16f;
                slot[0] = 1;
                slot[1] = B(e is NPC);
                slot[2] = B(e is Projectile);
                slot[3] = B(e is Item);
                slot[4] = delta.X / (ObservationConfig.ViewW / 2f);
                slot[5] = delta.Y / (ObservationConfig.ViewH / 2f);
                slot[6] = Clip(e.velocity.X / 10f, -2, 2);
                slot[7] = Clip(e.velocity.Y / 10f, -2, 2);
                slot[8] = Math.Min(e.width / 64f, 4);
                slot[9] = Math.Min(e.height / 64f, 4);
                switch (e) {
                    case NPC npc:
                        slot[10] = npc.lifeMax > 0 ? Clip(npc.life / (float)npc.lifeMax, 0, 1) : 0;
                        slot[11] = Math.Min(npc.damage / 100f, 2);
                        slot[12] = B(!npc.friendly && !npc.townNPC);
                        slot[13] = B(npc.boss);
                        break;
                    case Projectile proj:
                        slot[11] = Math.Min(proj.damage / 100f, 2);
                        slot[12] = B(proj.hostile);
                        break;
                }
            }
        }
    }
}
