namespace FirstMod_Sp.Common.Systems
{
    /// <summary>
    /// Wire protocol shared with Training/protocol.py. Every constant here has a twin on the
    /// Python side; change both together and bump PROTOCOL_VERSION (wire format) or
    /// OBS_VERSION (observation layout / meaning).
    ///
    /// All values are little-endian.
    ///
    /// Handshake (once per connection):
    ///   C# -> Py: uint32 MAGIC, int32 PROTOCOL_VERSION, int32 OBS_VERSION, int32 num_bots,
    ///             int32 STATS_SIZE, int32 GRID_SIDE, int32 frame_skip, int32 max_steps_per_zone,
    ///             int32 world_name_len, utf8 world_name
    ///   Py -> C#: int32 status (0 = ok), int32 msg_len, utf8 msg,
    ///             then if status == 0: num_bots x (int32 has_zone, float32 spawnX, spawnY, targetX, targetY)
    ///             = the zone each bot will start in.
    ///
    /// Decision tick (C# -> Py), N = num_bots:
    ///   N x STATS_SIZE float32 | N x GRID_CELLS float32 | N x (float32 path_dist, uint8 flags)
    ///   | for every bot with FLAG_DONE, in bot order: STATS_SIZE + GRID_CELLS float32 (terminal obs)
    /// Reply (Py -> C#), per bot: int32 command. If command >= ZONE_COMMAND_OFFSET the action is
    ///   command - ZONE_COMMAND_OFFSET and 4 float32 follow (spawnX, spawnY, targetX, targetY, pixels):
    ///   the zone the bot moves to when its current episode ends (queued, not applied immediately).
    /// </summary>
    public static class RLProtocol
    {
        public const uint MAGIC = 0x4C524954; // bytes "TIRL"
        public const int PROTOCOL_VERSION = 2;
        public const int OBS_VERSION = 2;

        public const int STATS_SIZE = RLInterface.STATS_SIZE;
        public const int GRID_SIDE = GridSensors.GRID_SIZE;
        public const int GRID_CELLS = GRID_SIDE * GRID_SIDE;

        public const int STATS_BYTES = STATS_SIZE * sizeof(float);
        public const int GRID_BYTES = GRID_CELLS * sizeof(float);
        public const int OBS_BYTES = STATS_BYTES + GRID_BYTES;
        public const int EXTRA_BYTES = sizeof(float) + sizeof(byte); // path_dist + flags

        public const int ZONE_COMMAND_OFFSET = 1_000_000;
        public const int ACTION_MASK = 0b111; // bit0 left, bit1 right, bit2 jump

        public const int HANDSHAKE_OK = 0;

        // Packet flags
        public const byte FLAG_DONE = 1;           // episode ended this tick; terminal obs follows
        public const byte FLAG_WIN = 2;
        public const byte FLAG_DIE = 4;
        public const byte FLAG_TIMEOUT = 8;
        public const byte FLAG_ABORTED = 16;       // ended without an outcome (bot despawned, training resumed)
        public const byte FLAG_ZONE_SWITCHED = 32; // bot moved to its queued zone this tick

        /// <summary>path_dist value meaning "no target for this bot".</summary>
        public const float NO_DISTANCE = -1f;

        /// <summary>Worst-case bytes for one decision packet (every bot done).</summary>
        public static int MaxPacketBytes(int numBots) => numBots * (OBS_BYTES + EXTRA_BYTES + OBS_BYTES);
    }
}
