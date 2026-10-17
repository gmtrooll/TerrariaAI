using System.Reflection;
using FirstMod_Sp.Common.Systems;
using Terraria;
using Terraria.Graphics;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Automation
{
    /// <summary>
    /// Makes fake bots work on a dedicated server with nobody connected. Vanilla breaks this in two ways,
    /// both in Netplay.UpdateConnectedClients, which the network thread runs about a thousand times a second:
    ///   1. it sets HasClients = false when no client is connected, and the server loop then skips Main.Update,
    ///      so the world (and every mod hook) is frozen until a person joins;
    ///   2. it sets Main.player[i].active = false for every slot without a connected client, which switches the
    ///      bots off as soon as they are spawned.
    /// The replacement below is the vanilla method with those two changes.
    /// </summary>
    public class ServerBotSupport : ModSystem
    {
        private static readonly MethodInfo SetPendingTermination =
            typeof(RemoteClient).GetMethod("SetPendingTermination", BindingFlags.Instance | BindingFlags.NonPublic);

        public override void Load() {
            if (!Main.dedServ || !AutomationArgs.Enabled) return;
            On_Netplay.UpdateConnectedClients += UpdateConnectedClients;
            On_Player.UpdateBiomes += SkipBotBiomes;
            On_Player.ForceUpdateBiomes += SkipBotForceBiomes;
            On_Lighting.Clear += SkipLightingClear;
        }

        public override void Unload() {
            On_Netplay.UpdateConnectedClients -= UpdateConnectedClients;
            On_Player.UpdateBiomes -= SkipBotBiomes;
            On_Player.ForceUpdateBiomes -= SkipBotForceBiomes;
            On_Lighting.Clear -= SkipLightingClear;
        }

        // Player.Spawn's local-player branch handles a far respawn as a camera jump: Lighting.Clear(), then
        // ForceUpdateBiomes(), which asks the camera for the visible area. A server has neither a lighting engine nor
        // a camera, so both threw and aborted the rest of Spawn (and with it "respawn where you died").
        private static void SkipLightingClear(On_Lighting.orig_Clear orig) {
        }

        private static void SkipBotForceBiomes(On_Player.orig_ForceUpdateBiomes orig, Player self) {
            if (!BotManagerSystem.IsBot[self.whoAmI]) orig(self);
        }

        public override void PostSetupContent() {
            if (!Main.dedServ || !AutomationArgs.Enabled) return;
            // Owner-only item code (aiming, instruments) reads the camera zoom and screen size, which a server
            // never sets up. A view matrix without a graphics device still answers Zoom = 1.
            Main.GameViewMatrix ??= new SpriteViewMatrix(null);
            if (Main.screenWidth <= 0 || Main.screenHeight <= 0) {
                Main.screenWidth = 1920;
                Main.screenHeight = 1080;
            }
        }

        // Player.Update applies controls (and calls SetControls) only while Main.hasFocus is set. The server's update
        // never sets it (BackgroundRunSystem's hook only reaches the client path), so set it before players update.
        public override void PreUpdatePlayers() {
            if (Main.dedServ && AutomationArgs.Enabled) Main.hasFocus = true;
        }

        // Bots update with Main.myPlayer set to themselves, so Player.Update takes its local-player paths.
        // UpdateBiomes is one of them: it drives sky and screen filters that do not exist on a server. A vanilla
        // server never runs it for remote players either (their zones arrive from the client).
        private static void SkipBotBiomes(On_Player.orig_UpdateBiomes orig, Player self) {
            if (!BotManagerSystem.IsBot[self.whoAmI]) orig(self);
        }

        private static void UpdateConnectedClients(On_Netplay.orig_UpdateConnectedClients orig) {
            for (int i = 0; i < 256; i++) {
                RemoteClient client = Netplay.Clients[i];
                if (client.PendingTermination) {
                    if (client.PendingTerminationApproved) {
                        client.State = 0;
                        NetMessage.SyncDisconnectedPlayer(i);
                        client.Reset();
                    }
                    continue;
                }
                if (client.IsConnected()) {
                    client.Update();
                    continue;
                }
                if (client.IsActive) {
                    SetPendingTermination.Invoke(client, new object[] { "Connection lost" });
                    client.PendingTerminationApproved = true;
                    continue;
                }
                client.StatusText2 = "";
                if (i < 255 && !BotManagerSystem.IsBot[i]) {
                    bool wasActive = Main.player[i].active;
                    Main.player[i].active = false;
                    if (wasActive) Player.Hooks.PlayerDisconnect(i);
                }
            }
            Netplay.HasClients = true;
        }
    }
}
