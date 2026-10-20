using System;
using System.Reflection;
using Microsoft.Xna.Framework.Audio;
using Terraria;
using Terraria.Audio;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Automation
{
    /// <summary>
    /// Game windows launched by the tools (-nosound) shut their audio down once loading is done. FAudio's Windows
    /// backend asserts and kills the process when the output device disappears (headphones unplugged:
    /// "Failed to get IAudioClient current padding!"), and this FNA build has no switch to start without audio.
    /// Teardown, in the order FNA itself uses at exit (SDL2_FNAPlatform.ProgramExit):
    ///   1. the music system (Main.audioSystem: XACT engine with its own FAudio instance) is disposed and replaced
    ///      by the game's DisabledAudioSystem;
    ///   2. SoundEngine.IsAudioSupported = false, so the game plays and updates no more sounds (every SoundEngine
    ///      path and Main.UpdateAudio check it);
    ///   3. FNA's SoundEffect device (FAudioContext) is disposed: destroying the mastering voice stops FAudio's
    ///      mixing thread, which is the thread that asserts.
    /// Anything that touches SoundEffect afterwards would only recreate the device (sound back on), not crash.
    /// </summary>
    public class NoSoundSystem : ModSystem
    {
        public override bool IsLoadingEnabled(Mod mod) => !Main.dedServ && Program.LaunchParameters.ContainsKey("-nosound");

        private static log4net.ILog log;

        public override void PostSetupContent() {
            log = Mod.Logger;
            Main.QueueMainThreadAction(Shutdown);
        }

        private static void Shutdown() {
            if (!SoundEngine.IsAudioSupported) return;
            try {
                SoundEngine.SoundPlayer?.StopAll();
                SoundEngine.StopAmbientSounds();
                Main.audioSystem?.Dispose();
                Main.audioSystem = new DisabledAudioSystem();
                typeof(SoundEngine).GetProperty(nameof(SoundEngine.IsAudioSupported)).GetSetMethod(true).Invoke(null, new object[] { false });

                Type context = typeof(SoundEffect).GetNestedType("FAudioContext", BindingFlags.NonPublic);
                object device = context?.GetField("Context", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (device != null) context.GetMethod("Dispose").Invoke(device, null);
                log.Info($"[NoSound] audio shut down (FNA device {(device != null ? "released" : "was not open")})");
            }
            catch (Exception e) {
                log.Warn($"[NoSound] audio shutdown failed: {e}");
            }
        }
    }
}
