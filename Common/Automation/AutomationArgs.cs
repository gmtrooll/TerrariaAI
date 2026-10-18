using Terraria;

namespace FirstMod_Sp.Common.Automation
{
    /// <summary>
    /// Launch parameters for automated runs. Without -bridgeport the mod behaves as it always did.
    ///   -bridgeport N   start the automation bridge on 127.0.0.1:N
    ///   -autobots N     spawn N bots when a world is entered
    ///   -rltrain        keep connecting to the Python trainer (off by default when the bridge is on)
    /// </summary>
    public static class AutomationArgs
    {
        public static readonly int BridgePort = GetIntArg("-bridgeport", 0);
        public static readonly int AutoBots = GetIntArg("-autobots", 0);

        public static bool Enabled => BridgePort > 0;

        /// <summary>The RL socket retries a blocking connect, which stalls the game for ~2 s per try on Windows.</summary>
        public static bool RlEnabled => !Enabled || Program.LaunchParameters.ContainsKey("-rltrain");

        public static int GetIntArg(string key, int fallback) {
            return Program.LaunchParameters.TryGetValue(key, out string value) && int.TryParse(value, out int n) ? n : fallback;
        }
    }
}
