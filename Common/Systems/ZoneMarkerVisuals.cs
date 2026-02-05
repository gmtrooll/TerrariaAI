using FirstMod_Sp.Content.Items.Tools;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.ModLoader;
using Terraria.GameContent;

namespace FirstMod_Sp.Common.Systems
{
    /// <summary>
    /// Draws visual markers for zone spawn/target points when using the ZoneMarkerTool
    /// </summary>
    public class ZoneMarkerVisuals : ModSystem
    {
        // Use PostDrawTiles to draw in world space (before UI transforms)
        public override void PostDrawTiles()
        {
            // Only draw when player has ZoneMarkerTool equipped
            Player player = Main.LocalPlayer;
            if (player.HeldItem?.ModItem is not ZoneMarkerTool) return;

            Vector2? spawn = ZoneMarkerTool.SpawnPoint;
            Vector2? target = ZoneMarkerTool.TargetPoint;

            // Need to start our own SpriteBatch since PostDrawTiles doesn't have one active
            SpriteBatch spriteBatch = Main.spriteBatch;
            spriteBatch.Begin(
                SpriteSortMode.Deferred,
                BlendState.AlphaBlend,
                SamplerState.PointClamp,
                DepthStencilState.None,
                RasterizerState.CullNone,
                null,
                Main.GameViewMatrix.ZoomMatrix
            );

            try
            {
                // Draw zone bounds preview first (so it's behind the markers)
                if (spawn.HasValue && target.HasValue)
                {
                    DrawZoneBoundsPreview(spriteBatch, spawn.Value, target.Value);
                    DrawLine(spriteBatch, spawn.Value, target.Value, Color.Yellow * 0.7f, 3);
                }

                // Draw spawn marker (green circle)
                if (spawn.HasValue)
                {
                    DrawMarker(spriteBatch, spawn.Value, Color.Lime, "SPAWN");
                }

                // Draw target marker (red circle)
                if (target.HasValue)
                {
                    DrawMarker(spriteBatch, target.Value, Color.Red, "TARGET");
                }
            }
            finally
            {
                spriteBatch.End();
            }
        }

        private void DrawMarker(SpriteBatch spriteBatch, Vector2 worldPos, Color color, string label)
        {
            // Convert world position to screen position
            Vector2 screenPos = worldPos - Main.screenPosition;
            
            Texture2D pixel = TextureAssets.MagicPixel.Value;
            int markerSize = 24;
            
            // Draw outer ring (larger, more visible)
            for (int i = 0; i < 360; i += 10)
            {
                float rad = MathHelper.ToRadians(i);
                Vector2 offset = new Vector2((float)System.Math.Cos(rad), (float)System.Math.Sin(rad)) * markerSize;
                spriteBatch.Draw(pixel, screenPos + offset, new Rectangle(0, 0, 6, 6), color, 0f, Vector2.One * 3, 1f, SpriteEffects.None, 0f);
            }
            
            // Draw inner ring
            for (int i = 0; i < 360; i += 15)
            {
                float rad = MathHelper.ToRadians(i);
                Vector2 offset = new Vector2((float)System.Math.Cos(rad), (float)System.Math.Sin(rad)) * (markerSize / 2);
                spriteBatch.Draw(pixel, screenPos + offset, new Rectangle(0, 0, 4, 4), color * 0.8f, 0f, Vector2.One * 2, 1f, SpriteEffects.None, 0f);
            }
            
            // Draw center dot
            spriteBatch.Draw(pixel, screenPos, new Rectangle(0, 0, 12, 12), color, 0f, Vector2.One * 6, 1f, SpriteEffects.None, 0f);
            
            // Draw label above marker
            Vector2 textPos = screenPos + new Vector2(-20, -40);
            Utils.DrawBorderString(spriteBatch, label, textPos, color, 0.9f);
        }

        private void DrawLine(SpriteBatch spriteBatch, Vector2 worldStart, Vector2 worldEnd, Color color, int thickness)
        {
            Vector2 screenStart = worldStart - Main.screenPosition;
            Vector2 screenEnd = worldEnd - Main.screenPosition;
            
            Texture2D pixel = TextureAssets.MagicPixel.Value;
            
            Vector2 direction = screenEnd - screenStart;
            float length = direction.Length();
            float rotation = (float)System.Math.Atan2(direction.Y, direction.X);
            
            spriteBatch.Draw(
                pixel, 
                screenStart, 
                new Rectangle(0, 0, (int)length, thickness), 
                color, 
                rotation, 
                new Vector2(0, thickness / 2f), 
                1f, 
                SpriteEffects.None, 
                0f
            );
        }

        private void DrawZoneBoundsPreview(SpriteBatch spriteBatch, Vector2 spawn, Vector2 target)
        {
            // Calculate zone bounds that ENCLOSE both points with padding
            // Calculate bounds with fixed padding around the markers
            float minX = System.Math.Min(spawn.X, target.X);
            float maxX = System.Math.Max(spawn.X, target.X);
            float minY = System.Math.Min(spawn.Y, target.Y);
            float maxY = System.Math.Max(spawn.Y, target.Y);
            
            // Fixed 50-tile padding in all directions
            float paddingPixels = 50f * 16f;  // 50 tiles = 800 pixels
            
            Vector2 topLeft = new Vector2(minX - paddingPixels, minY - paddingPixels) - Main.screenPosition;
            Vector2 topRight = new Vector2(maxX + paddingPixels, minY - paddingPixels) - Main.screenPosition;
            Vector2 bottomLeft = new Vector2(minX - paddingPixels, maxY + paddingPixels) - Main.screenPosition;
            Vector2 bottomRight = new Vector2(maxX + paddingPixels, maxY + paddingPixels) - Main.screenPosition;
            
            int zoneWidth = (int)((maxX - minX) / 16f + 100);  // +100 for padding both sides
            int zoneHeight = (int)((maxY - minY) / 16f + 100);
            
            // Draw zone boundary rectangle (thicker dashed lines)
            Color boundsColor = Color.Cyan * 0.6f;
            int lineThickness = 3;
            
            DrawDashedLine(spriteBatch, topLeft, topRight, boundsColor, lineThickness);
            DrawDashedLine(spriteBatch, topRight, bottomRight, boundsColor, lineThickness);
            DrawDashedLine(spriteBatch, bottomRight, bottomLeft, boundsColor, lineThickness);
            DrawDashedLine(spriteBatch, bottomLeft, topLeft, boundsColor, lineThickness);
            
            // Draw zone size label
            string sizeLabel = $"{zoneWidth}x{zoneHeight} tiles";
            Utils.DrawBorderString(spriteBatch, sizeLabel, topLeft + new Vector2(8, 8), Color.Cyan, 0.9f);
            
            // Also show distance info
            float distTiles = Vector2.Distance(spawn, target) / 16f;
            string distLabel = $"Distance: {distTiles:F0} tiles";
            Utils.DrawBorderString(spriteBatch, distLabel, topLeft + new Vector2(8, 28), Color.White * 0.8f, 0.7f);
        }

        private void DrawDashedLine(SpriteBatch spriteBatch, Vector2 start, Vector2 end, Color color, int thickness)
        {
            Texture2D pixel = TextureAssets.MagicPixel.Value;
            
            Vector2 direction = end - start;
            float length = direction.Length();
            float rotation = (float)System.Math.Atan2(direction.Y, direction.X);
            Vector2 normalized = Vector2.Normalize(direction);
            
            // Draw dashes (longer dashes, smaller gaps)
            int dashLength = 16;
            int gapLength = 8;
            int pos = 0;
            
            while (pos < length)
            {
                int drawLength = System.Math.Min(dashLength, (int)(length - pos));
                Vector2 dashStart = start + normalized * pos;
                
                spriteBatch.Draw(
                    pixel,
                    dashStart,
                    new Rectangle(0, 0, drawLength, thickness),
                    color,
                    rotation,
                    new Vector2(0, thickness / 2f),
                    1f,
                    SpriteEffects.None,
                    0f
                );
                
                pos += dashLength + gapLength;
            }
        }
    }
}
