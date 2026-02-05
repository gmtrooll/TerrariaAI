using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Systems
{
    /// <summary>
    /// Manages the zone database for RL training scenarios.
    /// Zones are saved to a JSON file and can be loaded for bot training.
    /// </summary>
    public class ZoneDatabase : ModSystem
    {
        public static ZoneDatabase Instance { get; private set; }
        
        public List<TrainingZone> Zones { get; private set; } = new List<TrainingZone>();
        
        private string DatabasePath => Path.Combine(
            Main.SavePath, 
            "ModSources", 
            "FirstMod_Sp", 
            "TrainingZones",
            "zones_database.json"
        );

        public override void Load()
        {
            Instance = this;
            LoadDatabase();
        }

        public override void Unload()
        {
            Instance = null;
        }

        /// <summary>
        /// Load zones from JSON file
        /// </summary>
        public void LoadDatabase()
        {
            try
            {
                if (File.Exists(DatabasePath))
                {
                    string json = File.ReadAllText(DatabasePath);
                    var data = JsonSerializer.Deserialize<ZoneDatabaseFile>(json);
                    Zones = data?.Zones ?? new List<TrainingZone>();
                    Main.NewText($"Loaded {Zones.Count} training zones.", 100, 255, 100);
                }
                else
                {
                    Zones = new List<TrainingZone>();
                    Main.NewText("No zone database found. Starting fresh.", 255, 255, 100);
                }
            }
            catch (Exception ex)
            {
                Main.NewText($"Error loading zones: {ex.Message}", 255, 100, 100);
                Zones = new List<TrainingZone>();
            }
        }

        /// <summary>
        /// Save zones to JSON file
        /// </summary>
        public void SaveDatabase()
        {
            try
            {
                // Ensure directory exists
                string dir = Path.GetDirectoryName(DatabasePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var data = new ZoneDatabaseFile { Zones = Zones };
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(data, options);
                File.WriteAllText(DatabasePath, json);
                
                Main.NewText($"Saved {Zones.Count} zones to database.", 100, 255, 100);
            }
            catch (Exception ex)
            {
                Main.NewText($"Error saving zones: {ex.Message}", 255, 100, 100);
            }
        }

        /// <summary>
        /// Add a new zone to the database
        /// </summary>
        public void AddZone(TrainingZone zone)
        {
            // Generate unique ID
            zone.ZoneId = $"zone_{DateTime.Now:yyyyMMdd_HHmmss}_{Zones.Count:D3}";
            zone.CreatedTimestamp = DateTime.Now.ToString("o");
            
            Zones.Add(zone);
            SaveDatabase();
            
            Main.NewText($"Zone '{zone.ZoneId}' added! (Difficulty: {zone.Difficulty})", 100, 255, 100);
        }

        /// <summary>
        /// Get a zone by ID
        /// </summary>
        public TrainingZone GetZone(string zoneId)
        {
            return Zones.Find(z => z.ZoneId == zoneId);
        }

        /// <summary>
        /// Get zones filtered by difficulty range
        /// </summary>
        public List<TrainingZone> GetZonesByDifficulty(int minDifficulty, int maxDifficulty)
        {
            return Zones.FindAll(z => z.Difficulty >= minDifficulty && z.Difficulty <= maxDifficulty);
        }

        /// <summary>
        /// Remove a zone by ID
        /// </summary>
        public bool RemoveZone(string zoneId)
        {
            var zone = GetZone(zoneId);
            if (zone != null)
            {
                Zones.Remove(zone);
                SaveDatabase();
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// JSON file structure
    /// </summary>
    public class ZoneDatabaseFile
    {
        public List<TrainingZone> Zones { get; set; } = new List<TrainingZone>();
    }

    /// <summary>
    /// Represents a training zone with spawn/target points and metadata
    /// </summary>
    public class TrainingZone
    {
        public string ZoneId { get; set; }
        
        // Original world location (for teleportation mode)
        public ZoneLocation OriginalLocation { get; set; } = new ZoneLocation();
        
        // Zone bounds in tile coordinates
        public ZoneBounds Bounds { get; set; } = new ZoneBounds();
        
        // Metadata
        public int Difficulty { get; set; } = 5;
        public string Description { get; set; } = "";
        public float DistanceTiles { get; set; }
        public float HeightDelta { get; set; }
        public string CreatedTimestamp { get; set; }
        
        // Terrain data (for instantiation mode - future)
        public string TerrainDataCompressed { get; set; }
        
        // Training statistics
        public ZoneStats Stats { get; set; } = new ZoneStats();
    }

    public class ZoneLocation
    {
        public float SpawnX { get; set; }
        public float SpawnY { get; set; }
        public float TargetX { get; set; }
        public float TargetY { get; set; }
        public string WorldName { get; set; }
    }

    public class ZoneBounds
    {
        public int MinX { get; set; }
        public int MaxX { get; set; }
        public int MinY { get; set; }
        public int MaxY { get; set; }
        public int Width => MaxX - MinX;
        public int Height => MaxY - MinY;
    }

    public class ZoneStats
    {
        public int TimesTrained { get; set; }
        public int SuccessfulAttempts { get; set; }
        public int FailedAttempts { get; set; }
        public float SuccessRate => TimesTrained > 0 ? (float)SuccessfulAttempts / TimesTrained : 0;
        public float AvgCompletionTime { get; set; }
        public float RecordTime { get; set; } = float.MaxValue;
        public string LastTrained { get; set; }
    }
}
