using System;
using Terraria;

namespace FirstMod_Sp.Common.Observation
{
    /// <summary>Grid layouts compared in the grid experiment (docs/Observation Design.md).</summary>
    public enum GridMode
    {
        /// <summary>41x41 tiles at one cell per tile, plus a 30x17 overview of the 120x68 view (4x4 tiles per cell).</summary>
        Fovea,
        /// <summary>40x40 tiles, the original observation's size.</summary>
        Square40,
        /// <summary>The whole 120x68 view at one cell per tile.</summary>
        Full,
    }

    /// <summary>Fairness options a, c and d (option b, the game's own lighting, does not exist on a server).</summary>
    public enum VisibilityMode
    {
        /// <summary>a: every tile in the view, including through rock and in the dark.</summary>
        All,
        /// <summary>c: exposed tiles (non-solid, or solid next to non-solid) lit by the sky above the surface or
        /// reached by a light flood from the bot through non-solid tiles within LightRadius.</summary>
        Exposed,
        /// <summary>d: c plus every tile this bot has seen before, flagged as remembered.</summary>
        ExposedMemory,
    }

    /// <summary>
    /// Observation settings. Launch: -obs grid=fovea,visibility=exposed,light=20,entities=0,waypoint=1
    /// At runtime: the bridge's observation_config command.
    /// </summary>
    public static class ObservationConfig
    {
        public static GridMode Grid = GridMode.Fovea;
        public static VisibilityMode Visibility = VisibilityMode.Exposed;
        public static int LightRadius = 20;
        public static bool Entities;
        /// <summary>Whether skills fill in the route waypoint (goal inputs); off = only the final target.</summary>
        public static bool Waypoint = true;
        /// <summary>Also a near waypoint (NearWaypointSteps ahead): the route's immediate direction, which the far
        /// waypoint hides when the route first turns away from the target. Off by default (older models).</summary>
        public static bool NearWaypoint;
        /// <summary>Far waypoint kept in sight: the furthest route point (up to WaypointSteps ahead) such that every
        /// route point up to it is in line of sight from the bot. Without it, a route that drops off a ledge and turns
        /// back puts the far waypoint under the ledge, pointing the wrong way (handmade tests, Oct 20).</summary>
        public static bool WaypointLos;
        /// <summary>A "blocked" self feature: decisions in a row pressing left or right while barely moving (snags on
        /// slopes and overhangs, where the bot pressed on for hundreds of decisions without trying a jump, Oct 20).</summary>
        public static bool Blocked;
        /// <summary>Measured gear abilities as capabilities (Env.Loadouts features: single-jump apex, total apex with every
        /// extra jump, extra jumps, top run speed), so gear is described by what it does (Oct 21).</summary>
        public static bool Gear;
        /// <summary>Refresh the overview every N observations of a bot (cached in between).</summary>
        public static int CoarseEvery = 4;
        public const int EntitySlots = 16;
        /// <summary>Incremented on every change, so per-bot caches built under old settings are dropped.</summary>
        public static int Version;

        public static int FineW => Grid switch { GridMode.Fovea => 41, GridMode.Square40 => 40, _ => 120 };
        public static int FineH => Grid switch { GridMode.Fovea => 41, GridMode.Square40 => 40, _ => 68 };
        public static bool HasCoarse => Grid == GridMode.Fovea;
        public const int ViewW = 120, ViewH = 68, CoarseCell = 4, CoarseW = ViewW / CoarseCell, CoarseH = ViewH / CoarseCell;

        public static void Apply(string spec) {
            foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                string[] kv = part.Split('=', 2);
                if (kv.Length != 2) throw new ArgumentException($"expected key=value, got '{part}'");
                Set(kv[0].Trim().ToLowerInvariant(), kv[1].Trim());
            }
        }

        public static void Set(string key, string value) {
            Version++;
            switch (key) {
                case "grid":
                    Grid = Enum.Parse<GridMode>(value, ignoreCase: true);
                    break;
                case "visibility":
                    Visibility = Enum.Parse<VisibilityMode>(value, ignoreCase: true);
                    break;
                case "light":
                    LightRadius = Math.Clamp(int.Parse(value), 0, 60);
                    break;
                case "coarse_every":
                    CoarseEvery = Math.Clamp(int.Parse(value), 1, 60);
                    break;
                case "entities":
                    Entities = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "waypoint":
                    Waypoint = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "near":
                    NearWaypoint = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "los":
                    WaypointLos = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "blocked":
                    Blocked = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                case "gear":
                    Gear = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
                default:
                    throw new ArgumentException($"unknown observation setting '{key}' (grid, visibility, light, coarse_every, entities, waypoint, near, los, blocked, gear)");
            }
        }

        public static void LoadFromLaunchArgs() {
            Grid = GridMode.Fovea;
            Visibility = VisibilityMode.Exposed;
            LightRadius = 20;
            Entities = false;
            Waypoint = true;
            NearWaypoint = false;
            WaypointLos = false;
            Blocked = false;
            Gear = false;
            CoarseEvery = 4;
            if (Program.LaunchParameters.TryGetValue("-obs", out string spec) && !string.IsNullOrWhiteSpace(spec)) Apply(spec);
        }
    }
}
