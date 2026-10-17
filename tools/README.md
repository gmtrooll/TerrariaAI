# Terraria MCP

Lets an agent (or a script) build the mod, launch tModLoader, act in the game, inspect it and run C# inside it. Two halves:

- **Mod side**, in `Common/Automation/`: a JSON-over-TCP bridge that runs commands on the game thread between ticks. It is active only when the game is started with `-bridgeport N`; without it the mod behaves as before.
- **Python side**, in `tools/terraria_mcp/`:
  - `game.py`: build, launch, stop and generate worlds; shared by everything
  - `bridge.py`: the bridge client
  - `server.py`: the MCP server, registered in the repository's `.mcp.json`

## Setup

```bash
cd tools
python -m venv .venv
.venv/Scripts/python.exe -m pip install -r requirements-lock.txt
```

Paths default to this machine's layout. They can be overridden with environment variables (see `config.py`): `TML_DIR`, `TML_MOD_SOURCE`, `TML_TRAIN_SAVE_DIR`.

## Building

`build_mod` builds into a staging save directory (`tModLoader-Train/BuildStage`, through tModLoader's `-tmlsavedirectory` build flag). No game ever loads from there. A running game keeps its `.tmod` open, and building into a folder a game uses fails with TML003. Launching copies the staged `.tmod` into the training `Mods` folder, but only when it changed.

## Measuring

- **`bench.py`** (`python -m terraria_mcp.bench run ...`, or the `run_bench` tool). The mod's `bench` command runs warmup + N ticks back to back with a deterministic random policy. It reports where each tick's time goes, using tModLoader's own hooks around `Main.DoUpdateInWorld` (players, npcs, gores, projectiles, items, dusts, time, world, outside). It also reports per-bot cost, CPU, RAM, allocations and garbage collections. Rows go to `results/bench/bench.csv`.
  - The first seconds after a launch are 2–3× slower (JIT and detour compilation), hence the 3,000-tick default warmup.
- **`physics_check`.** The same start positions and actions, with enemies cleared and spawns blocked, run twice per mode. A result of 0.0 means bit-identical trajectories.
- **`profile.py`** (or the `profile` tool). Samples the game thread with `dotnet-trace`, which lives in `tml-reference/tools`. It reports inclusive and exclusive time per method and the callers of a chosen root. Detoured methods appear as `DMD<...Type:Method>`.

## How a launch works

- **Isolated save directory.** Everything runs in `Documents/My Games/Terraria/tModLoader-Train` (`-tmlsavedirectory`). It holds only this mod, training worlds and a character named `Automation`, which the mod creates on first client launch. Personal worlds, characters and mods are never touched.
- **Client:** `-skipselect Automation:<world>` loads straight into the world. The launcher writes the settings an unattended client needs into the training `config.json`:
  - `Language`, otherwise the game stops at the language picker
  - `ThrottleWhenInactive` off
  - `AutoPause` off
- **Server:** `-server -world <path>`, headless. Two vanilla behaviours would break fake bots on a server with nobody connected; `ServerBotSupport.cs` patches both:
  - the server skips world updates when no client is connected
  - the network thread deactivates every player slot without a client
- **Bots:** `-autobots N` spawns N bots when the world is entered.
- **RL socket:** the old connection to the Python trainer is off in automated runs unless `-rltrain` is passed. Each refused connect attempt stalls the game for about 2 s on Windows.

## Tools

| Group | Tools |
| --- | --- |
| Session | `build_mod`, `launch`, `stop`, `stop_all`, `instances`, `rebuild_and_relaunch`, `logs`, `worlds`, `create_world` |
| Run code | `execute_csharp` (method body; `Print`, `Me`, `Bots`, `Bot(n)` helpers), `wait_until` (C# condition checked every tick) |
| Time | `pause`, `resume`, `step`, `set_speed` |
| Observe | `status`, `get_state`, `get_tiles` (text grid), `render_map` (image from tile data), `screenshot` (client), `get_entities`, `errors` |
| Act | `input` (local player or bot, held N ticks), `spawn_bots`, `despawn_bots`, `teleport`, `give`, `spawn_npc`, `set_time` |
| Measurement | `run_bench`, `physics_check`, `profile` |
| Escape hatch | `bridge_command` |

Positions are in tiles. Prefer `get_state`, `get_tiles` and `render_map` to `screenshot`: they are cheaper and also work on a headless server.

## Using the core from a script

```python
import sys; sys.path.insert(0, "tools")
from terraria_mcp.game import GameManager

g = GameManager()
g.build()
g.launch("server", bots=4)
print(g.call(None, "execute", code="Bots.Count"))
g.stop_all()
```
