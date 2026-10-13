using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Terraria;

namespace FirstMod_Sp.Common.Systems
{
    /// <summary>
    /// Loads training configuration from Training/training_config.json (the same file Python reads).
    /// num_bots must match Python's value; the connection handshake rejects a mismatch.
    /// </summary>
    public static class TrainingConfigLoader
    {
        private static JObject _config = null;
        private static bool _loaded = false;

        public static int NumBots { get; private set; } = 16;
        public static int Port { get; private set; } = 5555;
        public static int MaxStepsPerZone { get; private set; } = 1500;
        /// <summary>Game ticks per decision: each action is applied for exactly this many ticks.</summary>
        public static int FrameSkip { get; private set; } = 4;
        public static bool Debug { get; private set; } = false;
        public static int MaxJumpTiles { get; private set; } = 14;
        public static int PathFieldRadius { get; private set; } = 96;
        public static float OffPathPenaltyTiles { get; private set; } = 10f;
        public static string ConfigPath { get; private set; } = null;

        /// <summary>Load config from JSON file. Pass force=true to re-read it (done on every world load).</summary>
        public static void LoadConfig(bool force = false)
        {
            if (_loaded && !force) return;

            UseDefaults();
            try
            {
                ConfigPath = FindConfigFile();

                if (string.IsNullOrEmpty(ConfigPath))
                {
                    Main.NewText("[Config] WARNING: training_config.json not found, using defaults", 255, 200, 100);
                    _loaded = true;
                    return;
                }

                _config = JObject.Parse(File.ReadAllText(ConfigPath));

                NumBots = GetValue("core_settings.num_bots", NumBots);
                Port = GetValue("core_settings.port", Port);
                MaxStepsPerZone = GetValue("core_settings.max_steps_per_zone", MaxStepsPerZone);
                FrameSkip = Math.Max(1, GetValue("observation_settings.frame_skip", FrameSkip));
                Debug = GetValue("logging.debug", Debug);
                MaxJumpTiles = GetValue("path_settings.max_jump_tiles", MaxJumpTiles);
                PathFieldRadius = GetValue("path_settings.field_radius_tiles", PathFieldRadius);
                OffPathPenaltyTiles = GetValue("path_settings.off_path_penalty_tiles", OffPathPenaltyTiles);

                Main.NewText($"[Config] Loaded {ConfigPath}: {NumBots} bots, port {Port}, timeout {MaxStepsPerZone}, frame_skip {FrameSkip}", 100, 255, 100);
                _loaded = true;
            }
            catch (Exception ex)
            {
                Main.NewText($"[Config] ERROR reading {ConfigPath}: {ex.Message} (using defaults)", 255, 100, 100);
                UseDefaults();
                _loaded = true;
            }
        }

        private static string FindConfigFile()
        {
            string[] possiblePaths = new string[]
            {
                // Mod source folder (where README says to clone the repo); Main.SavePath is the tModLoader folder
                Path.Combine(Main.SavePath, "ModSources", "FirstMod_Sp", "Training", "training_config.json"),
                // Default Windows Documents location (in case SavePath was redirected)
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "My Games", "Terraria", "tModLoader", "ModSources", "FirstMod_Sp", "Training", "training_config.json"),
                // Current directory
                "training_config.json",
                Path.Combine("Training", "training_config.json")
            };

            foreach (string path in possiblePaths)
            {
                if (File.Exists(path)) return Path.GetFullPath(path);
            }

            return null;
        }

        private static T GetValue<T>(string path, T defaultValue)
        {
            try
            {
                JToken token = _config;
                foreach (string part in path.Split('.'))
                {
                    token = token[part];
                    if (token == null) return defaultValue;
                }
                return token.Value<T>();
            }
            catch
            {
                return defaultValue;
            }
        }

        private static void UseDefaults()
        {
            NumBots = 16;
            Port = 5555;
            MaxStepsPerZone = 1500;
            FrameSkip = 4;
            Debug = false;
            MaxJumpTiles = 14;
            PathFieldRadius = 96;
            OffPathPenaltyTiles = 10f;
        }
    }
}
