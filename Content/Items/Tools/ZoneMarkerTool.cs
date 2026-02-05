using System;
using FirstMod_Sp.Common.Systems;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace FirstMod_Sp.Content.Items.Tools
{
    /// <summary>
    /// Tool for marking and capturing training zones.
    /// Left-click: Set spawn point
    /// Right-click: Set target point
    /// Hold C + Click: Capture zone
    /// Hold D + Click: Set difficulty (1-9 based on number key)
    /// </summary>
    public class ZoneMarkerTool : ModItem
    {
        // Use a map icon texture as placeholder
        public override string Texture => "Terraria/Images/Item_" + ItemID.TreasureMap;

        // Current marked positions (in world coordinates)
        private static Vector2? _spawnPoint = null;
        private static Vector2? _targetPoint = null;
        private static int _currentDifficulty = 5;
        private static string _currentDescription = "";
        
        // Zone browsing
        private static int _currentZoneIndex = -1;
        private static TrainingZone _currentBrowsedZone = null;

        public static Vector2? SpawnPoint => _spawnPoint;
        public static Vector2? TargetPoint => _targetPoint;
        public static TrainingZone CurrentBrowsedZone => _currentBrowsedZone;

        public override void SetStaticDefaults()
        {
            Item.ResearchUnlockCount = 1;
        }

        public override void SetDefaults()
        {
            Item.width = 28;
            Item.height = 28;
            Item.useTime = 15;
            Item.useAnimation = 15;
            Item.useStyle = ItemUseStyleID.Swing;
            Item.value = 0;
            Item.rare = ItemRarityID.Cyan;
            Item.autoReuse = false;
        }

        public override void ModifyTooltips(System.Collections.Generic.List<TooltipLine> tooltips)
        {
            tooltips.Add(new TooltipLine(Mod, "Controls1", "[c/00FF00:Left Click] - Set spawn point"));
            tooltips.Add(new TooltipLine(Mod, "Controls2", "[c/FF0000:Right Click] - Set target point"));
            tooltips.Add(new TooltipLine(Mod, "Controls3", "[c/FFFF00:C + Click] - Capture zone"));
            tooltips.Add(new TooltipLine(Mod, "Controls4", "[c/FFAA00:X + Click] - Clear markers"));
            tooltips.Add(new TooltipLine(Mod, "Controls5", "[c/00FFFF:Q + Click] - Browse prev zone"));
            tooltips.Add(new TooltipLine(Mod, "Controls6", "[c/00FFFF:E + Click] - Browse next zone"));
            tooltips.Add(new TooltipLine(Mod, "Controls7", "[c/FF00FF:G + Click] - Teleport to zone"));
            tooltips.Add(new TooltipLine(Mod, "Controls8", "[c/FF0000:Alt + Click] - Delete zone near cursor"));
            tooltips.Add(new TooltipLine(Mod, "Controls9", "[c/AAAAAA:, + Click] - Decrease difficulty"));
            tooltips.Add(new TooltipLine(Mod, "Controls10", "[c/AAAAAA:. + Click] - Increase difficulty"));
        }

        public override bool AltFunctionUse(Player player) => true;

        public override bool? UseItem(Player player)
        {
            if (player.whoAmI != Main.myPlayer) return true;

            Vector2 mouseWorld = Main.MouseWorld;
            bool isRightClick = player.altFunctionUse == 2;
            
            // Check for modifier keys
            bool holdingC = Main.keyState.IsKeyDown(Keys.C);
            bool holdingX = Main.keyState.IsKeyDown(Keys.X);
            bool holdingQ = Main.keyState.IsKeyDown(Keys.Q);
            bool holdingE = Main.keyState.IsKeyDown(Keys.E);
            bool holdingG = Main.keyState.IsKeyDown(Keys.G);
            bool holdingAlt = Main.keyState.IsKeyDown(Keys.LeftAlt) || Main.keyState.IsKeyDown(Keys.RightAlt);
            bool holdingComma = Main.keyState.IsKeyDown(Keys.OemComma);    // < key
            bool holdingPeriod = Main.keyState.IsKeyDown(Keys.OemPeriod);  // > key

            // --- DELETE ZONE (Alt + Click) ---
            if (holdingAlt)
            {
                DeleteZoneAtCursor(mouseWorld);
                return true;
            }

            // --- CAPTURE ZONE (Hold C + Click) ---
            if (holdingC)
            {
                CaptureZone();
                return true;
            }

            // --- CLEAR MARKERS (Hold X + Click) ---
            if (holdingX)
            {
                ClearMarkers();
                return true;
            }

            // --- BROWSE ZONES (Q = previous, E = next) ---
            if (holdingQ)
            {
                BrowseZone(-1);
                return true;
            }
            if (holdingE)
            {
                BrowseZone(1);
                return true;
            }

            // --- TELEPORT TO ZONE (G + Click) ---
            if (holdingG)
            {
                TeleportToCurrentZone(player);
                return true;
            }

            // --- CHANGE DIFFICULTY (< = decrease, > = increase) ---
            if (holdingComma)
            {
                _currentDifficulty = Math.Max(1, _currentDifficulty - 1);
                Main.NewText($"Zone difficulty: {_currentDifficulty}/10", 255, 255, 100);
                return true;
            }
            if (holdingPeriod)
            {
                _currentDifficulty = Math.Min(10, _currentDifficulty + 1);
                Main.NewText($"Zone difficulty: {_currentDifficulty}/10", 255, 255, 100);
                return true;
            }

            // --- SET SPAWN POINT (Left Click) ---
            if (!isRightClick)
            {
                _spawnPoint = mouseWorld;
                Vector2 tileLoc = mouseWorld / 16f;
                Main.NewText($"Spawn point set at ({tileLoc.X:F1}, {tileLoc.Y:F1})", 100, 255, 100);
                
                // Show distance if both points set
                if (_targetPoint.HasValue)
                    ShowZoneInfo();
                    
                return true;
            }

            // --- SET TARGET POINT (Right Click) ---
            if (isRightClick)
            {
                _targetPoint = mouseWorld;
                Vector2 tileLoc = mouseWorld / 16f;
                Main.NewText($"Target point set at ({tileLoc.X:F1}, {tileLoc.Y:F1})", 255, 100, 100);
                
                // Show distance if both points set
                if (_spawnPoint.HasValue)
                    ShowZoneInfo();
                    
                return true;
            }

            return true;
        }

        private void ShowZoneInfo()
        {
            if (!_spawnPoint.HasValue || !_targetPoint.HasValue) return;
            
            float distPixels = Vector2.Distance(_spawnPoint.Value, _targetPoint.Value);
            float distTiles = distPixels / 16f;
            float heightDelta = (_targetPoint.Value.Y - _spawnPoint.Value.Y) / 16f;
            
            Main.NewText($"Distance: {distTiles:F1} tiles | Height: {heightDelta:F1} tiles | Difficulty: {_currentDifficulty}", 200, 200, 255);
        }

        private void CaptureZone()
        {
            if (!_spawnPoint.HasValue || !_targetPoint.HasValue)
            {
                Main.NewText("Set both spawn (left-click) and target (right-click) first!", 255, 100, 100);
                return;
            }

            Vector2 spawn = _spawnPoint.Value;
            Vector2 target = _targetPoint.Value;
            
            float distTiles = Vector2.Distance(spawn, target) / 16f;
            float heightDelta = (target.Y - spawn.Y) / 16f;
            
            // Calculate bounds with fixed padding around the markers
            float minX = Math.Min(spawn.X, target.X);
            float maxX = Math.Max(spawn.X, target.X);
            float minY = Math.Min(spawn.Y, target.Y);
            float maxY = Math.Max(spawn.Y, target.Y);
            
            // Fixed 50-tile padding in all directions
            float paddingPixels = 50f * 16f;  // 50 tiles = 800 pixels
            
            int minTileX = (int)((minX - paddingPixels) / 16f);
            int maxTileX = (int)((maxX + paddingPixels) / 16f);
            int minTileY = (int)((minY - paddingPixels) / 16f);
            int maxTileY = (int)((maxY + paddingPixels) / 16f);

            // Create zone object
            var zone = new TrainingZone
            {
                OriginalLocation = new ZoneLocation
                {
                    SpawnX = spawn.X,
                    SpawnY = spawn.Y,
                    TargetX = target.X,
                    TargetY = target.Y,
                    WorldName = Main.worldName
                },
                Bounds = new ZoneBounds
                {
                    MinX = minTileX,
                    MaxX = maxTileX,
                    MinY = minTileY,
                    MaxY = maxTileY
                },
                Difficulty = _currentDifficulty,
                Description = _currentDescription,
                DistanceTiles = distTiles,
                HeightDelta = heightDelta
            };

            // Capture terrain data (compressed for future use)
            zone.TerrainDataCompressed = CaptureTerrainData(zone.Bounds);

            // Add to database
            ZoneDatabase.Instance?.AddZone(zone);
            
            int zoneWidth = maxTileX - minTileX;
            int zoneHeight = maxTileY - minTileY;
            Main.NewText($"Zone captured! Size: {zoneWidth}x{zoneHeight}, Distance: {distTiles:F1} tiles", 100, 255, 100);
            
            // Clear markers after capture
            // (Keep them so user can see what was captured)
        }

        private string CaptureTerrainData(ZoneBounds bounds)
        {
            // Capture tile data for the zone
            // For now, just store tile type IDs in a simple format
            // Future: Compress with RLE or gzip
            
            try
            {
                var tiles = new System.Collections.Generic.List<ushort>();
                var walls = new System.Collections.Generic.List<ushort>();
                
                for (int y = bounds.MinY; y < bounds.MaxY; y++)
                {
                    for (int x = bounds.MinX; x < bounds.MaxX; x++)
                    {
                        if (x >= 0 && x < Main.maxTilesX && y >= 0 && y < Main.maxTilesY)
                        {
                            Tile tile = Main.tile[x, y];
                            tiles.Add(tile.TileType);
                            walls.Add(tile.WallType);
                        }
                        else
                        {
                            tiles.Add(0);
                            walls.Add(0);
                        }
                    }
                }
                
                // Convert to base64 for storage
                byte[] tileBytes = new byte[tiles.Count * 2];
                for (int i = 0; i < tiles.Count; i++)
                {
                    tileBytes[i * 2] = (byte)(tiles[i] & 0xFF);
                    tileBytes[i * 2 + 1] = (byte)(tiles[i] >> 8);
                }
                
                return Convert.ToBase64String(tileBytes);
            }
            catch (Exception ex)
            {
                Main.NewText($"Terrain capture error: {ex.Message}", 255, 100, 100);
                return "";
            }
        }

        private void ClearMarkers()
        {
            _spawnPoint = null;
            _targetPoint = null;
            _currentBrowsedZone = null;
            _currentZoneIndex = -1;
            Main.NewText("Markers cleared.", 255, 255, 100);
        }

        private void BrowseZone(int direction)
        {
            var db = ZoneDatabase.Instance;
            if (db == null || db.Zones.Count == 0)
            {
                Main.NewText("No zones in database! Capture some zones first.", 255, 100, 100);
                return;
            }

            // Navigate through zones
            _currentZoneIndex += direction;
            
            // Wrap around
            if (_currentZoneIndex < 0)
                _currentZoneIndex = db.Zones.Count - 1;
            if (_currentZoneIndex >= db.Zones.Count)
                _currentZoneIndex = 0;

            _currentBrowsedZone = db.Zones[_currentZoneIndex];
            
            // Update visual markers to show this zone
            _spawnPoint = new Vector2(_currentBrowsedZone.OriginalLocation.SpawnX, _currentBrowsedZone.OriginalLocation.SpawnY);
            _targetPoint = new Vector2(_currentBrowsedZone.OriginalLocation.TargetX, _currentBrowsedZone.OriginalLocation.TargetY);
            
            Main.NewText($"Zone {_currentZoneIndex + 1}/{db.Zones.Count}: '{_currentBrowsedZone.ZoneId}' | Difficulty: {_currentBrowsedZone.Difficulty}/10 | Distance: {_currentBrowsedZone.DistanceTiles:F0} tiles", 100, 200, 255);
        }

        private void DeleteZoneAtCursor(Vector2 mouseWorld)
        {
            var db = ZoneDatabase.Instance;
            if (db == null || db.Zones.Count == 0)
            {
                Main.NewText("No zones in database to delete!", 255, 100, 100);
                return;
            }

            // Find zone near cursor (check spawn and target points)
            TrainingZone nearestZone = null;
            float nearestDist = float.MaxValue;
            const float DETECTION_RANGE = 80f; // 5 tiles in pixels (16 pixels per tile)

            foreach (var zone in db.Zones)
            {
                Vector2 spawnPos = new Vector2(zone.OriginalLocation.SpawnX, zone.OriginalLocation.SpawnY);
                Vector2 targetPos = new Vector2(zone.OriginalLocation.TargetX, zone.OriginalLocation.TargetY);
                
                float distToSpawn = Vector2.Distance(mouseWorld, spawnPos);
                float distToTarget = Vector2.Distance(mouseWorld, targetPos);
                float minDist = Math.Min(distToSpawn, distToTarget);
                
                if (minDist < DETECTION_RANGE && minDist < nearestDist)
                {
                    nearestZone = zone;
                    nearestDist = minDist;
                }
            }

            if (nearestZone != null)
            {
                // Confirm deletion
                string zoneId = nearestZone.ZoneId;
                if (db.RemoveZone(zoneId))
                {
                    db.SaveDatabase();
                    Main.NewText($"Deleted zone '{zoneId}'!", 255, 100, 100);
                    
                    // Clear browsed zone if it was the deleted one
                    if (_currentBrowsedZone?.ZoneId == zoneId)
                    {
                        _currentBrowsedZone = null;
                        _currentZoneIndex = -1;
                    }
                    
                    // Visual feedback
                    for (int i = 0; i < 20; i++)
                    {
                        Dust.NewDust(new Vector2(nearestZone.OriginalLocation.SpawnX, nearestZone.OriginalLocation.SpawnY), 
                            16, 16, DustID.Smoke, 0f, 0f, 100, default, 2f);
                    }
                }
                else
                {
                    Main.NewText("Failed to delete zone!", 255, 100, 100);
                }
            }
            else
            {
                Main.NewText("No zone found near cursor! Get within 5 tiles of a zone marker.", 255, 200, 100);
            }
        }

        private void TeleportToCurrentZone(Player player)
        {
            if (_currentBrowsedZone == null)
            {
                Main.NewText("No zone selected! Use Q/E + Click to browse zones first.", 255, 100, 100);
                return;
            }

            // Teleport player to this zone's spawn point
            Vector2 spawnPos = new Vector2(
                _currentBrowsedZone.OriginalLocation.SpawnX,
                _currentBrowsedZone.OriginalLocation.SpawnY
            );
            
            player.Teleport(spawnPos, TeleportationStyleID.RodOfDiscord);
            
            Main.NewText($"Teleported to zone '{_currentBrowsedZone.ZoneId}' spawn point!", 100, 255, 100);
        }

        public override void AddRecipes()
        {
            CreateRecipe()
                .AddIngredient(ItemID.DirtBlock, 1)
                .AddTile(TileID.WorkBenches)
                .Register();
        }
    }
}
