using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace FirstMod_Sp.Common.Automation
{
    /// <summary>
    /// Screenshots from the back buffer at the end of Main.DoDraw, before the frame is presented. Other windows
    /// covering the game do not appear in them. No frame is drawn while the window is minimized, so requests
    /// then time out.
    /// </summary>
    public static class ScreenCapture
    {
        private const int TimeoutMs = 5000;

        private static readonly List<(BridgeRequest request, DateTime deadline)> pending = new List<(BridgeRequest, DateTime)>();
        private static int counter;

        public static string Folder => Path.Combine(Main.SavePath, "Automation", "screenshots");

        public static void Load() {
            On_Main.DoDraw += DoDraw;
        }

        public static void Unload() {
            On_Main.DoDraw -= DoDraw;
            pending.Clear();
        }

        public static void Request(BridgeRequest request) {
            pending.Add((request, DateTime.UtcNow.AddMilliseconds(TimeoutMs)));
        }

        /// <summary>Fails requests that waited too long; called from the update loop, which runs even without drawing.</summary>
        public static void ExpireStale() {
            for (int i = pending.Count - 1; i >= 0; i--) {
                if (DateTime.UtcNow < pending[i].deadline) continue;
                pending[i].request.Fail("no frame was drawn within 5 s (is the window minimized?)");
                pending.RemoveAt(i);
            }
        }

        private static void DoDraw(On_Main.orig_DoDraw orig, Main self, GameTime gameTime) {
            orig(self, gameTime);
            if (pending.Count == 0) return;

            string path;
            int width, height;
            try {
                GraphicsDevice device = Main.instance.GraphicsDevice;
                width = device.PresentationParameters.BackBufferWidth;
                height = device.PresentationParameters.BackBufferHeight;
                var pixels = new Color[width * height];
                device.GetBackBufferData(pixels);
                for (int i = 0; i < pixels.Length; i++) pixels[i].A = 255;

                Directory.CreateDirectory(Folder);
                path = Path.Combine(Folder, $"shot_{DateTime.Now:yyyyMMdd_HHmmss}_{++counter}.png");
                using var texture = new Texture2D(device, width, height);
                texture.SetData(pixels);
                using var stream = File.Create(path);
                texture.SaveAsPng(stream, width, height);
            }
            catch (Exception e) {
                foreach (var (request, _) in pending) request.Fail("screenshot failed: " + BridgeCommands.Describe(e));
                pending.Clear();
                return;
            }

            foreach (var (request, _) in pending) {
                request.Reply(new { path, width, height, tick = BridgeSystem.Tick });
            }
            pending.Clear();
        }
    }
}
