using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using System.IO;
using Terraria;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;

namespace FirstMod_Sp.Common.Systems 
{
    // 1. Register the Keybind
    public class GridKeybindSystem : ModSystem
    {
        public static ModKeybind ExtractGridKey { get; private set; }

        public override void Load()
        {
            // Registers a keybind named "Extract Grid" bound to 'P'
            ExtractGridKey = KeybindLoader.RegisterKeybind(Mod, "Extract Grid", Keys.P);
        }

        public override void Unload()
        {
            ExtractGridKey = null;
        }
    }

    // 2. Handle the Player Logic and File Writing
    public class GridExtractionPlayer : ModPlayer
    {
        public override void ProcessTriggers(TriggersSet triggersSet)
        {
            if (GridKeybindSystem.ExtractGridKey.JustPressed)
            {
                ExtractGrid();
            }
        }

        // In GridExtractionPlayer.cs
    private void ExtractGrid()
    {
        // 1. Get the raw data using the helper
        float[] data = GridSensors.GetObservationGrid(Player);

        string debugFolder = Path.Combine(Main.SavePath, "DebugOutput");
        Directory.CreateDirectory(debugFolder);
        string filePath = Path.Combine(debugFolder, "grid_dump.txt");

        using (StreamWriter writer = new StreamWriter(filePath))
        {
           writer.WriteLine("Grid Dump (40x40)");
            // Reconstruct 2D shape for text file readability
            for (int i = 0; i < data.Length; i++)
            {
                writer.Write($"{data[i]} ");
               // New line every 40 items
               if ((i + 1) % GridSensors.GRID_SIZE == 0) writer.WriteLine();
           }
        }
        Main.NewText($"Grid saved to {filePath}");
    }
    }
}