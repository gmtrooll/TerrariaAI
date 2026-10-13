using System;
using System.Collections.Generic;
using Terraria;
using Terraria.ModLoader;
using Microsoft.Xna.Framework;

namespace FirstMod_Sp.Common.Systems
{
    // =================================================================================
    // FAKE BOT CONTROLLER
    // Use this to test RLInterface.ApplyAction without running the Python script.
    // =================================================================================
    public class FakeBotController : ModSystem
    {
        public static bool TestingEnabled = false; 
        
        // State Machine Storage
        private class TestState {
            public int ActionTimer;
            public int CurrentAction; // 0=Idle, 1=Left, 2=Right, 3=Hook, 4=JumpSpam
            public Vector2 CachedAim;
        }

        // Store a state for each bot so they don't flicker between actions every frame
        private Dictionary<int, TestState> _botStates = new Dictionary<int, TestState>();
        private Random _rng = new Random();

        public override void PreUpdatePlayers() {
            if (!TestingEnabled) return;

            foreach (int botIndex in BotManagerSystem.ActiveBots) {
                // Ensure state exists
                if (!_botStates.ContainsKey(botIndex)) {
                    _botStates[botIndex] = new TestState();
                }

                float[] fakeActions = GenerateRandomPattern(botIndex, _botStates[botIndex]);
                // Only left/right/jump reach the bot (same action space as the RL agent).
                int bits = 0;
                if (fakeActions[0] > 0.5f) bits |= 1; // Left
                if (fakeActions[1] > 0.5f) bits |= 2; // Right
                if (fakeActions[4] > 0.5f) bits |= 4; // Jump
                RLInterface.ApplyActionBits(botIndex, bits);
            }
        }

        public override void Unload() {
            _botStates.Clear();
        }

        private float[] GenerateRandomPattern(int botIndex, TestState state) {
            // [0] Left, [1] Right, [2] Up, [3] Down, [4] Jump, [5] Hook, [6] Aim X, [7] Aim Y
            float[] actions = new float[8];

            // 1. STATE MACHINE TRANSITIONS
            // If timer expired, pick a new random action
            if (state.ActionTimer <= 0) {
                // Pick random duration (30 to 120 frames)
                state.ActionTimer = _rng.Next(30, 120);
                
                // Pick random Action Type
                // 0-30: Run Left
                // 31-60: Run Right
                // 61-75: Jump Spam
                // 76-85: Hook
                // 86-100: Idle/Aim
                int roll = _rng.Next(0, 100);

                if (roll < 30) state.CurrentAction = 1; // Left
                else if (roll < 60) state.CurrentAction = 2; // Right
                else if (roll < 75) state.CurrentAction = 4; // Jump
                else if (roll < 85) {
                    state.CurrentAction = 3; // Hook
                    state.ActionTimer = 20; // Hooks are quick actions
                    // Pick a random direction for the hook
                    float angle = (float)(_rng.NextDouble() * Math.PI * 2);
                    state.CachedAim = new Vector2((float)Math.Cos(angle) * 300f, (float)Math.Sin(angle) * 300f);
                }
                else state.CurrentAction = 0; // Idle

                // If not hooking, occasionally update aim to look around randomly
                if (state.CurrentAction != 3) {
                     state.CachedAim = new Vector2(_rng.Next(-200, 201), _rng.Next(-200, 201));
                }
            }

            state.ActionTimer--;

            // 2. FILL OUTPUTS BASED ON STATE
            
            // Aiming (Default to CachedAim)
            actions[6] = state.CachedAim.X;
            actions[7] = state.CachedAim.Y;

            switch (state.CurrentAction) {
                case 1: // Run Left
                    actions[0] = 1f;
                    // Occasional random jump while running (10% chance per frame)
                    if (_rng.NextDouble() < 0.1) actions[4] = 1f;
                    break;

                case 2: // Run Right
                    actions[1] = 1f;
                    if (_rng.NextDouble() < 0.1) actions[4] = 1f;
                    break;

                case 3: // Hook
                    // To successfully hook, we aim and press the button
                    actions[5] = 1f; 
                    // While hooking, aim towards the cached aim point
                    actions[6] = state.CachedAim.X;
                    actions[7] = state.CachedAim.Y;
                    break;

                case 4: // Jump Spam (Flying/Panic)
                    actions[4] = 1f; // Hold Jump
                    // Randomly drift left or right while flying
                    if (_rng.NextDouble() > 0.5) actions[0] = 1f;
                    else actions[1] = 1f;
                    break;
                
                case 0: // Idle
                    // Just look around, maybe crouch
                    if (_rng.NextDouble() > 0.95) actions[3] = 1f; // Down
                    break;
            }

            return actions;
        }
    }

    public class ToggleTestCommand : ModCommand {
        public override CommandType Type => CommandType.Chat;
        public override string Command => "testbots";
        public override string Usage => "/testbots";
        public override string Description => "Toggles the Fake Bot Controller for testing movement.";

        public override void Action(CommandCaller caller, string input, string[] args) {
            FakeBotController.TestingEnabled = !FakeBotController.TestingEnabled;
            Main.NewText($"Fake Bot Testing: {(FakeBotController.TestingEnabled ? "ENABLED" : "DISABLED")}", 
                FakeBotController.TestingEnabled ? Color.Green : Color.Red);
        }
    }
}