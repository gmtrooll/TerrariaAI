using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FirstMod_Sp.Common.Systems;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.IO;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Automation
{
    /// <summary>
    /// Owns the game loop while automation is on: runs bridge commands on the game thread before each frame's
    /// update, controls how many ticks a frame runs (pause, step, speed) and completes deferred replies
    /// (step, wait_until, input with wait) when their condition is met. Works the same in client and server mode,
    /// because both run Main.DoUpdate once per frame.
    /// </summary>
    public class BridgeSystem : ModSystem
    {
        public const int MaxTicksPerFrame = 2000;

        /// <summary>Ticks run in a world since launch.</summary>
        public static long Tick;
        public static bool Paused;
        public static int Speed = 1;
        public static int StepsLeft;

        private static readonly List<Waiter> waiters = new List<Waiter>();
        private static bool autoSpawnPending;
        private static bool quitRequested;
        private static bool quitSave;

        /// <summary>A reply that is sent once Done() holds, or OnTimeout() at the deadline.</summary>
        public sealed class Waiter
        {
            public BridgeRequest Request;
            public Func<bool> Done;
            public Func<object> Result;
            public Func<object> OnTimeout;
            public long DeadlineTick = long.MaxValue;
            public DateTime DeadlineUtc = DateTime.MaxValue;
        }

        public static void AddWaiter(Waiter waiter) {
            waiters.Add(waiter);
        }

        public static void RequestQuit(bool save) {
            quitRequested = true;
            quitSave = save;
        }

        public override void Load() {
            if (!AutomationArgs.Enabled) return;
            On_Main.DoUpdate += DoUpdate;
            // Player.Update only applies controls (and calls SetControls) while the game has focus; a server never
            // has it, and an unattended client loses it whenever another window is in front.
            BackgroundRunSystem.RunInBackground = true;
            if (!Main.dedServ) ScreenCapture.Load();
            BridgeServer.Start(AutomationArgs.BridgePort);
            string message = $"[Automation] bridge listening on 127.0.0.1:{AutomationArgs.BridgePort}";
            Mod.Logger.Info(message);
            Console.WriteLine(message);
        }

        public override void Unload() {
            On_Main.DoUpdate -= DoUpdate;
            ScreenCapture.Unload();
            BridgeServer.Stop();
            waiters.Clear();
            InputHolds.ByPlayer.Clear();
        }

        /// <summary>Character used by -skipselect in automated client runs; created on first launch.</summary>
        public const string AutomationPlayerName = "Automation";

        public override void PostSetupContent() {
            if (!AutomationArgs.Enabled || Main.dedServ) return;
            string path = Main.GetPlayerPathFromName(AutomationPlayerName, false);
            if (File.Exists(path)) return;
            // An exception here would make tModLoader disable the whole mod, so only log it.
            try {
                Directory.CreateDirectory(Main.PlayerPath);
                var player = new Player { name = AutomationPlayerName };
                Player.SavePlayer(new PlayerFileData(path, false) {
                    Metadata = FileMetadata.FromCurrentSettings(FileType.Player),
                    Name = AutomationPlayerName,
                    Player = player,
                }, skipMapSave: true);
                Mod.Logger.Info("[Automation] created player " + path);
            }
            catch (Exception e) {
                Mod.Logger.Error("[Automation] could not create the automation player: " + e);
            }
        }

        public override void OnWorldLoad() {
            autoSpawnPending = AutomationArgs.Enabled && AutomationArgs.AutoBots > 0;
        }

        public override void PreUpdatePlayers() {
            if (!AutomationArgs.Enabled) return;
            if (autoSpawnPending) {
                autoSpawnPending = false;
                BridgeCommands.SpawnBots(AutomationArgs.AutoBots, null, 2);
            }
            InputHolds.Advance();
        }

        /// <summary>Counts real world updates. On a client, DoUpdate runs more often than that and returns early
        /// until 1/60 s has accumulated, so ticks are counted here rather than per DoUpdate call.</summary>
        public override void PostUpdateEverything() {
            if (!AutomationArgs.Enabled) return;
            Tick++;
            if (Paused && StepsLeft > 0) StepsLeft--;
            CheckWaiters();
        }

        private static void DoUpdate(On_Main.orig_DoUpdate orig, Main self, ref GameTime gameTime) {
            Pump();
            if (!Main.dedServ) ScreenCapture.ExpireStale();

            if (Main.gameMenu) {
                RunUpdate(orig, self, ref gameTime);
            }
            else if (Paused) {
                if (StepsLeft > 0) RunFast(orig, self, ref gameTime, () => StepsLeft > 0);
            }
            else if (Speed > 1) {
                long target = Tick + Speed;
                RunFast(orig, self, ref gameTime, () => Tick < target);
            }
            else {
                RunUpdate(orig, self, ref gameTime);
            }

            CheckWaiters(); // wall-clock timeouts, also while nothing ticks
            if (quitRequested) Quit();
        }

        /// <summary>Runs world updates back to back. Main.superFast makes a client skip its 60 Hz time accumulator,
        /// so every call is a real tick; a server has no accumulator.</summary>
        private static void RunFast(On_Main.orig_DoUpdate orig, Main self, ref GameTime gameTime, Func<bool> more) {
            bool wasSuperFast = Main.superFast;
            Main.superFast = true;
            try {
                for (int calls = 0; calls < MaxTicksPerFrame && more(); calls++) RunUpdate(orig, self, ref gameTime);
            }
            finally {
                Main.superFast = wasSuperFast;
            }
        }

        /// <summary>
        /// An exception inside the update would otherwise abort the rest of the tick silently (tModLoader only logs
        /// it). Record it so status and the errors command show it, and keep the tick count going.
        /// </summary>
        private static void RunUpdate(On_Main.orig_DoUpdate orig, Main self, ref GameTime gameTime) {
            try {
                orig(self, ref gameTime);
            }
            catch (Exception e) {
                ErrorCount++;
                string message = $"{e.GetType().Name}: {e.Message}";
                if (RecentErrors.Count > 0 && RecentErrors[^1].message == message) {
                    RecentErrors[^1].count++;
                    RecentErrors[^1].last_tick = Tick;
                    return;
                }
                // Detoured game methods (DMD<...>) have no line numbers; the IL offset still pinpoints the instruction
                // (compare with `ilspycmd -il` output of the original method).
                string where = string.Join("\n", new System.Diagnostics.StackTrace(e, false).GetFrames().Take(10)
                    .Select(f => $"{f.GetMethod()?.DeclaringType?.Name}.{f.GetMethod()?.Name} IL_{Math.Max(0, f.GetILOffset()):X4}"));
                RecentErrors.Add(new GameError { message = message, where = where, first_tick = Tick, last_tick = Tick, count = 1 });
                if (RecentErrors.Count > 20) RecentErrors.RemoveAt(0);
                ModContent.GetInstance<BridgeSystem>().Mod.Logger.Warn("[Automation] update exception: " + e);
            }
        }

        public static int ErrorCount;
        public static readonly List<GameError> RecentErrors = new List<GameError>();

        public sealed class GameError
        {
            public string message;
            public string where;
            public long first_tick;
            public long last_tick;
            public int count;
        }

        private static void Pump() {
            int budget = 64;
            while (budget-- > 0 && BridgeServer.Pending.TryDequeue(out BridgeRequest request)) {
                BridgeCommands.Dispatch(request);
            }
        }

        private static void CheckWaiters() {
            for (int i = waiters.Count - 1; i >= 0; i--) {
                Waiter waiter = waiters[i];
                object result;
                try {
                    if (waiter.Done()) {
                        result = waiter.Result();
                    }
                    else if (Tick >= waiter.DeadlineTick || DateTime.UtcNow >= waiter.DeadlineUtc) {
                        result = waiter.OnTimeout();
                    }
                    else {
                        continue;
                    }
                }
                catch (Exception e) {
                    waiters.RemoveAt(i);
                    waiter.Request.Fail(BridgeCommands.Describe(e));
                    continue;
                }
                waiters.RemoveAt(i);
                waiter.Request.Reply(result);
            }
        }

        private static void Quit() {
            quitRequested = false;
            if (Main.dedServ) {
                Netplay.SaveOnServerExit = quitSave;
                Netplay.Disconnect = true;
            }
            else {
                Main.instance.Exit();
            }
        }
    }

    /// <summary>
    /// Inputs held for a number of ticks, per player slot. Bots get them through BotData (cleared after every bot
    /// update, so they are written again each tick); the local player through AutomationInputPlayer.SetControls.
    /// </summary>
    public static class InputHolds
    {
        public sealed class Hold
        {
            public bool Left, Right, Up, Down, Jump, Hook, Use;
            /// <summary>Aim point in world pixels, or null for the default aim.</summary>
            public Vector2? Aim;
            public int TicksLeft;
            public bool Applied;
        }

        public static readonly Dictionary<int, Hold> ByPlayer = new Dictionary<int, Hold>();
        private static readonly List<int> expired = new List<int>();

        /// <summary>Called once per tick before players update: count down holds used last tick, then apply the rest to bots.</summary>
        public static void Advance() {
            expired.Clear();
            foreach (var (index, hold) in ByPlayer) {
                if (hold.Applied && --hold.TicksLeft <= 0) expired.Add(index);
            }
            foreach (int index in expired) ByPlayer.Remove(index);

            foreach (var (index, hold) in ByPlayer) {
                hold.Applied = true;
                if (!BotManagerSystem.IsBot[index] || !BotManagerSystem.BotStates.TryGetValue(index, out BotData data)) continue;
                data.ControlLeft = hold.Left;
                data.ControlRight = hold.Right;
                data.ControlUp = hold.Up;
                data.ControlDown = hold.Down;
                data.ControlJump = hold.Jump;
                data.ControlHook = hold.Hook;
                data.ControlUseItem = hold.Use;
                data.AimDirection = hold.Aim.HasValue ? hold.Aim.Value - Main.player[index].Center : Vector2.Zero;
            }
        }

        public static void ApplyToLocalPlayer(Player player) {
            if (!ByPlayer.TryGetValue(player.whoAmI, out Hold hold)) return;
            player.controlLeft = hold.Left;
            player.controlRight = hold.Right;
            player.controlUp = hold.Up;
            player.controlDown = hold.Down;
            player.controlJump = hold.Jump;
            player.controlHook = hold.Hook;
            player.controlUseItem = hold.Use;
            if (hold.Aim is Vector2 aim) {
                Main.mouseX = (int)(aim.X - Main.screenPosition.X);
                Main.mouseY = (int)(aim.Y - Main.screenPosition.Y);
            }
        }
    }

    public class AutomationInputPlayer : ModPlayer
    {
        public override void SetControls() {
            if (!AutomationArgs.Enabled || BotManagerSystem.IsBot[Player.whoAmI] || Player.whoAmI != Main.myPlayer) return;
            InputHolds.ApplyToLocalPlayer(Player);
        }
    }
}
