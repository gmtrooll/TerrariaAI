using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Terraria;

namespace FirstMod_Sp.Common.Systems
{
    /// <summary>
    /// Loads training configuration from training_config.json
    /// </summary>
    public static class TrainingConfigLoader
    {
        private static JObject _config = null;
        private static bool _loaded = false;

        public static int NumBots { get; private set; } = 16;
        public static int Port { get; private set; } = 5555;
        public static int MaxStepsPerZone { get; private set; } = 1500;
        public static int FrameSkip { get; private set; } = 4;

        /// <summary>
        /// Load config from JSON file
        /// </summary>
        public static void LoadConfig()
        {
            if (_loaded) return;

            try
            {
                // Find config file
                string configPath = FindConfigFile();
                
                if (string.IsNullOrEmpty(configPath))
                {
                    Main.NewText("[Config] WARNING: training_config.json not found, using defaults", 255, 200, 100);
                    UseDefaults();
                    _loaded = true;
                    return;
                }

                // Read and parse JSON
                string jsonText = File.ReadAllText(configPath);
                _config = JObject.Parse(jsonText);

                // Extract values
                NumBots = GetInt("core_settings.num_bots", 16);
                Port = GetInt("core_settings.port", 5555);
                MaxStepsPerZone = GetInt("core_settings.max_steps_per_zone", 1500);
                FrameSkip = GetInt("observation_settings.frame_skip", 4);

                Main.NewText($"[Config] Loaded: {NumBots} bots, port {Port}, timeout {MaxStepsPerZone}", 100, 255, 100);
                _loaded = true;
            }
            catch (Exception ex)
            {
                Main.NewText($"[Config] ERROR: {ex.Message}", 255, 100, 100);
                UseDefaults();
                _loaded = true;
            }
        }

        private static string FindConfigFile()
        {
            // Try multiple possible locations
            string[] possiblePaths = new string[]
            {
                // Relative to mod source
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), 
                    "My Games", "Terraria", "tModLoader", "ModSources", "FirstMod_Sp", "Training", "training_config.json"),
                // Current directory
                "training_config.json",
                // Training folder relative to exe
                Path.Combine("Training", "training_config.json")
            };

            foreach (string path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        private static int GetInt(string path, int defaultValue)
        {
            try
            {
                string[] parts = path.Split('.');
                JToken token = _config;
                
                foreach (string part in parts)
                {
                    token = token[part];
                    if (token == null) return defaultValue;
                }

                return token.Value<int>();
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
        }
    }
}
