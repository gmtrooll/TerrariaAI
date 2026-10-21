using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FirstMod_Sp.Common.Automation;
using FirstMod_Sp.Common.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Benchmark
{
    /// <summary>
    /// The "bench" bridge command: runs warmup + N ticks back to back (paused + step, so as fast as the CPU allows),
    /// drives every bot with a deterministic random policy and reports where the time went.
    ///
    /// Sections are timed between tModLoader's own hooks around Main.DoUpdateInWorld (see the decompiled
    /// Main.DoUpdateInWorld): players = PreUpdatePlayers..PreUpdateNPCs (includes the bots, which BotManagerSystem
    /// updates in PostUpdatePlayers), npcs, gores, projectiles, items, dusts (also rain, clouds, popup text),
    /// time = UpdateTime (clock, NPC spawning), world = WorldGen.UpdateWorld, invasions and server networking.
    /// "outside" is everything else in a tick: input, UI, audio and the bridge's own loop.
    /// </summary>
    public class BenchSystem : ModSystem
    {
        private static readonly string[] SectionNames = { "players", "npcs", "gores", "projectiles", "items", "dusts", "time", "world" };

        private sealed class Run
        {
            public BridgeRequest Request;
            public int Warmup, Ticks, FrameSkip, Seed;
            public bool Record, FixEnvironment, Isolate;
            public bool Observe => ObserveMode != "none";
            public string Policy;
            public string ObserveMode;
            public bool WasPaused;
            public int TickIndex;
            public bool Measuring;
            public int MeasureFrom;
            public double WarmupSeconds;
            public long LaunchedAt = Stopwatch.GetTimestamp();

            public long WallStart;
            public TimeSpan CpuStart;
            public long AllocStart, BotTimeStart;
            public int Gc0, Gc1, Gc2, ErrorsStart;
            public long ObserveTime;
            public readonly long[] SectionTime = new long[SectionNames.Length];
            public List<int> Bots;
            public List<float>[] Trajectories;
        }

        private static Run run;
        private static int currentSection = -1;
        private static long sectionStart;
        private static byte[] observationBytes = Array.Empty<byte>();

        public override void Load() {
            if (AutomationArgs.Enabled) BridgeCommands.Register("bench", Start);
        }

        public override void Unload() {
            run = null;
        }

        private static void Start(BridgeRequest r) {
            if (run != null) {
                r.Fail("a benchmark is already running");
                return;
            }
            if (Main.gameMenu) {
                r.Fail("not in a world");
                return;
            }
            var a = r.Args;
            run = new Run {
                Request = r,
                // The first seconds after launch run 2-3x slower while the JIT optimizes hot code and detours compile.
                Warmup = Math.Max(0, a.Value<int?>("warmup") ?? 3000),
                WarmupSeconds = Math.Max(0, a.Value<double?>("warmup_seconds") ?? 5),
                Ticks = Math.Max(1, a.Value<int?>("ticks") ?? 3000),
                FrameSkip = Math.Max(1, a.Value<int?>("frame_skip") ?? 4),
                Seed = a.Value<int?>("seed") ?? 1,
                // observe: false, true (= "new") or "new"
                ObserveMode = a["observe"]?.Type == Newtonsoft.Json.Linq.JTokenType.String ? a.Value<string>("observe")
                    : a.Value<bool?>("observe") == true ? "new" : "none",
                Record = a.Value<bool?>("record") ?? false,
                FixEnvironment = a.Value<bool?>("fix_environment") ?? true,
                Policy = a.Value<string>("policy") ?? "random",
                WasPaused = BridgeSystem.Paused,
                Bots = BotManagerSystem.ActiveBots.ToList(),
            };
            if (run.Policy != "random" && run.Policy != "idle") {
                run = null;
                r.Fail("policy must be 'random' or 'idle'");
                return;
            }
            InputHolds.ByPlayer.Clear();
            run.Isolate = a.Value<bool?>("isolate") ?? false;
            if (run.Isolate) {
                BenchIsolation.ClearWorld();
                BenchIsolation.BlockSpawns = true;
            }
            if (a.Value<bool?>("place_bots") == true) {
                PlaceBots(a.Value<float?>("x") ?? Main.spawnTileX, a.Value<float?>("y") ?? Main.spawnTileY - 3, a.Value<float?>("spread") ?? 2f);
            }
            if (run.Record) run.Trajectories = run.Bots.Select(_ => new List<float>()).ToArray();
            if (run.Warmup == 0 && run.WarmupSeconds == 0) BeginMeasuring();
            // The benchmark owns stepping until it finishes: warmup can last longer than its tick count.
            BridgeSystem.Paused = true;
            BridgeSystem.StepsLeft = int.MaxValue / 2;
        }

        /// <summary>Same start state for every run: bot i at a fixed tile, at rest, full life.</summary>
        private static void PlaceBots(float x, float y, float spread) {
            for (int i = 0; i < run.Bots.Count; i++) {
                Player bot = Main.player[run.Bots[i]];
                bot.Center = new Vector2((x + (i - (run.Bots.Count - 1) / 2f) * spread) * 16f + 8f, y * 16f + 8f);
                bot.velocity = Vector2.Zero;
                bot.fallStart = bot.fallStart2 = (int)(bot.position.Y / 16f);
                bot.statLife = bot.statLifeMax2;
                bot.jump = 0;
                bot.immuneTime = 0;
            }
        }

        private static void BeginMeasuring() {
            run.Measuring = true;
            Array.Clear(run.SectionTime);
            run.ObserveTime = 0;
            run.BotTimeStart = BotManagerSystem.UpdateTime;
            run.AllocStart = GC.GetTotalAllocatedBytes();
            run.Gc0 = GC.CollectionCount(0);
            run.Gc1 = GC.CollectionCount(1);
            run.Gc2 = GC.CollectionCount(2);
            run.ErrorsStart = BridgeSystem.ErrorCount;
            run.CpuStart = Process.GetCurrentProcess().TotalProcessorTime;
            run.WallStart = Stopwatch.GetTimestamp();
        }

        // ---------- per-tick hooks ----------

        private static void Mark(int section) {
            long now = Stopwatch.GetTimestamp();
            if (run != null && run.Measuring && currentSection >= 0) run.SectionTime[currentSection] += now - sectionStart;
            currentSection = section;
            sectionStart = now;
        }

        public override void PreUpdatePlayers() {
            Mark(0);
            if (run == null) return;
            if (run.FixEnvironment) {
                // Wind pushes airborne players on the surface; keep it, and the time of day, constant.
                Main.windSpeedCurrent = Main.windSpeedTarget = 0f;
                Main.dayTime = true;
                Main.time = 27000;
            }
            ApplyPolicy();
        }

        public override void PreUpdateNPCs() => Mark(1);
        public override void PreUpdateGores() => Mark(2);
        public override void PreUpdateProjectiles() => Mark(3);
        public override void PreUpdateItems() => Mark(4);
        public override void PreUpdateDusts() => Mark(5);
        public override void PreUpdateTime() => Mark(6);
        public override void PostUpdateTime() => Mark(7);

        public override void PostUpdateEverything() {
            Mark(-1);
            if (run == null) return;

            run.TickIndex++;
            bool decisionEnd = run.TickIndex % run.FrameSkip == 0;
            if (run.Measuring && decisionEnd) {
                if (run.Observe) Observe();
                if (run.Record) Record();
            }
            if (!run.Measuring && run.TickIndex >= run.Warmup && Stopwatch.GetElapsedTime(run.LaunchedAt).TotalSeconds >= run.WarmupSeconds) {
                // Warmup ends after both the tick count and a minimum time: the JIT promotes hot code on a timer,
                // so a few thousand very fast ticks are not enough.
                run.MeasureFrom = run.TickIndex;
                BeginMeasuring();
            }
            if (run.Measuring && run.TickIndex - run.MeasureFrom >= run.Ticks) Finish();
        }

        /// <summary>Each bot holds one of the 8 combinations of left/right/jump for a whole decision
        /// (FrameSkip ticks), chosen by a hash of (seed, bot, decision), so every run sees the same actions.</summary>
        private static void ApplyPolicy() {
            int decision = run.TickIndex / run.FrameSkip;
            for (int i = 0; i < run.Bots.Count; i++) {
                if (!BotManagerSystem.BotStates.TryGetValue(run.Bots[i], out BotData data)) continue;
                int action = run.Policy == "random" ? (int)(Hash((uint)run.Seed, (uint)i, (uint)decision) % 8) : 0;
                data.ControlLeft = (action & 1) != 0;
                data.ControlRight = (action & 2) != 0;
                data.ControlJump = (action & 4) != 0;
            }
        }

        private static uint Hash(uint a, uint b, uint c) {
            uint h = a * 0x9E3779B1u ^ b * 0x85EBCA77u ^ c * 0xC2B2AE3Du;
            h ^= h >> 15;
            h *= 0x2C1B3C6Du;
            h ^= h >> 12;
            h *= 0x297A2D39u;
            h ^= h >> 15;
            return h;
        }

        /// <summary>What the RL loop builds per bot per decision: the observation of docs/Observation Design.md
        /// (ObservationBuilder, current observation_config). (The original 40x40 float grid and 23 stats, "legacy",
        /// went with the first pipeline: tag legacy-zones-pipeline.)</summary>
        private static void Observe() {
            long start = Stopwatch.GetTimestamp();
            foreach (int index in run.Bots) {
                Player bot = Main.player[index];
                var op = bot.GetModPlayer<Observation.ObservationPlayer>();
                op.Target ??= bot.Center + new Vector2(320f, 0f);
                if (observationBytes.Length < Observation.ObservationBuilder.TotalBytes) observationBytes = new byte[Observation.ObservationBuilder.TotalBytes];
                Observation.ObservationBuilder.Build(bot, observationBytes);
            }
            run.ObserveTime += Stopwatch.GetTimestamp() - start;
        }

        private static void Record() {
            for (int i = 0; i < run.Bots.Count; i++) {
                Player bot = Main.player[run.Bots[i]];
                run.Trajectories[i].Add(bot.Center.X / 16f);
                run.Trajectories[i].Add(bot.Center.Y / 16f);
            }
        }

        private static void Finish() {
            Run done = run;
            run = null;
            BridgeSystem.StepsLeft = 0;
            BridgeSystem.Paused = done.WasPaused;
            if (done.Isolate) BenchIsolation.BlockSpawns = false;
            foreach (int index in done.Bots) {
                if (BotManagerSystem.BotStates.TryGetValue(index, out BotData data)) data.Reset();
            }

            long wallTicks = Stopwatch.GetTimestamp() - done.WallStart;
            double wall = wallTicks / (double)Stopwatch.Frequency;
            double msPerTick = wall * 1000.0 / done.Ticks;
            double ToMsPerTick(long stopwatchTicks) => stopwatchTicks * 1000.0 / Stopwatch.Frequency / done.Ticks;

            var sections = new Dictionary<string, double>();
            double sectionSum = 0;
            for (int i = 0; i < SectionNames.Length; i++) {
                double ms = ToMsPerTick(done.SectionTime[i]);
                sections[SectionNames[i]] = Math.Round(ms, 4);
                sectionSum += ms;
            }
            double observeMs = ToMsPerTick(done.ObserveTime);
            sections["observe"] = Math.Round(observeMs, 4);
            sections["outside"] = Math.Round(msPerTick - sectionSum - observeMs, 4);
            double botsMs = ToMsPerTick(BotManagerSystem.UpdateTime - done.BotTimeStart);

            Process process = Process.GetCurrentProcess();
            int bots = done.Bots.Count;
            done.Request.Reply(new {
                side = Main.dedServ ? "server" : "client",
                bots,
                ticks = done.Ticks,
                frame_skip = done.FrameSkip,
                observe = done.ObserveMode,
                observation_bytes = done.ObserveMode == "new" ? Observation.ObservationBuilder.TotalBytes : 0,
                observation = done.ObserveMode == "new" ? Observation.ObservationSystem.Layout() : null,
                wall_s = Math.Round(wall, 3),
                ticks_per_s = Math.Round(done.Ticks / wall, 1),
                decisions_per_s = Math.Round(bots * done.Ticks / (double)done.FrameSkip / wall, 1),
                ms_per_tick = Math.Round(msPerTick, 4),
                sections_ms = sections,
                bot_updates_ms = Math.Round(botsMs, 4),
                us_per_bot_tick = bots > 0 ? Math.Round(botsMs * 1000.0 / bots, 2) : 0,
                cpu_cores = Math.Round((process.TotalProcessorTime - done.CpuStart).TotalSeconds / wall, 2),
                working_set_mb = process.WorkingSet64 / (1024 * 1024),
                private_mb = process.PrivateMemorySize64 / (1024 * 1024),
                managed_mb = GC.GetTotalMemory(false) / (1024 * 1024),
                alloc_kb_per_tick = Math.Round((GC.GetTotalAllocatedBytes() - done.AllocStart) / 1024.0 / done.Ticks, 2),
                gc = new { gen0 = GC.CollectionCount(0) - done.Gc0, gen1 = GC.CollectionCount(1) - done.Gc1, gen2 = GC.CollectionCount(2) - done.Gc2 },
                errors = BridgeSystem.ErrorCount - done.ErrorsStart,
                fidelity_off = Fidelity.FidelitySystem.Switches.Where(s => !Fidelity.FidelitySystem.On(s)).ToList(),
                bot_lean = Bots.BotLeanUpdate.Enabled,
                world = new { Main.worldName, w = Main.maxTilesX, h = Main.maxTilesY },
                active = new {
                    npcs = Main.npc.Count(n => n.active),
                    projectiles = Main.projectile.Count(p => p.active),
                    items = Main.item.Count(i => i.active),
                    dusts = Main.dust.Count(d => d.active),
                    gores = Main.gore.Count(g => g.active),
                },
                trajectories = done.Trajectories?.Select(t => t.Select(v => Math.Round(v, 3)).ToList()).ToList(),
            });
        }
    }
}
