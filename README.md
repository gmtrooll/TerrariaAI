# Terraria Reinforcement Learning Mod

A custom tModLoader mod and Python training environment for teaching AI bots to play Terraria.

This project sets up a full system for training bots in Terraria using Proximal Policy Optimization (PPO). It uses a real-time connection to link the C# game engine with the Python training loop.

## Architecture & Technologies

The project is split into two main parts that constantly talk to each other over a fast TCP socket connection:

### 1. Game Environment (C# / tModLoader)

- **Language:** C#
- **Framework:** tModLoader (.NET)
- Grabs the live game state, including a "vision grid" of the blocks around the player and the player's stats.
- Takes action commands from the Python side and applies them directly to the player in-game.
- Calculates distances and basic reward flags, then packs them up and sends them to the Python server.

### 2. Training Pipeline (Python)

- **Language:** Python
- **ML Frameworks:** Stable Baselines 3, PyTorch, Gymnasium
- **Algorithm:** Proximal Policy Optimization (PPO)
- Trains multiple bots at the same time using vectorized environments (`VecEnv`). Frame stacking (`VecFrameStack`) can be turned on with `frame_stack` in the config to give the bots memory of what just happened (off by default).
- Includes a live Streamlit dashboard (`dashboard.py`) to track how the training is going, watch model performance, and handle save files.

### Communication Layer (TCP Sockets)

To make sure actions happen instantly without lag, the C# mod and Python script talk over local TCP sockets. The C# client packs the game data (vision grids, stats) into tight binary buffers. The Python server reads this data, runs it through the AI model, and sends back a binary number that tells each bot what buttons to press (like move left, move right, or jump).

The exact byte layout lives in two files that must change together: `Common/Systems/RLProtocol.cs` and `Training/protocol.py` (protocol version 2, observation version 2). In short:

1. **Handshake.** When the mod connects it sends a header (magic, protocol version, observation version, `num_bots`, stats size, grid size, `frame_skip`, `max_steps_per_zone`, world name). Python checks it and refuses the connection with a readable message (shown in the console and in the game chat) if anything differs, e.g. `num_bots`. Otherwise it answers with the first zone of every bot. Zones recorded in another world are ignored.
2. **Every decision** (every `frame_skip` game ticks) the mod sends, for all bots: the stats vectors, the 40x40 grids, then per bot a path distance (float) and a flag byte (done, win, died, timeout, aborted, zone switched), then the last observation of every bot whose episode just ended.
3. Python answers with one action per bot (bit 1 left, 2 right, 4 jump). It also sends each bot's *next* zone ahead of time, so when an episode ends the mod teleports the bot in the same tick and the observation Python gets is already the first one of the new episode.

The mod sends a packet, then waits for the actions, then applies them for the next `frame_skip` ticks. `reset()` reads the first packet, so the observation and reward returned by `step(a)` always describe the state after `a`.

### Observation

- `vision`: 40x40 tiles around the bot (0 air, 0.5 platform, 1 solid).
- `stats` (23 values, scaled to about [-1, 1]): velocity, facing, position inside the tile, jump frames left, Tsunami in a Bottle double jump available/active, liquid and debuff flags, fall distance, health, immunity frames, and the bot's **own zone target**: a has-target flag, the offset clamped to 20 tiles, the unit direction and a log-scaled distance. The full list is `STATS_FIELDS` in `Training/protocol.py`.

### Reward

`Training/reward_system.py`: +20 for reaching the target, -5 for dying, -1 for a timeout, plus `0.5 x` tiles of progress per step. Progress is measured with a path distance computed in the mod (`Common/Systems/PathDistanceField.cs`): a breadth-first search over the world from the target, built once per target, where the bot can walk, fall, and jump up at most `max_jump_tiles` above the ground. All weights are in `training_config.json`. Timeouts are reported to Stable-Baselines3 as truncations (not real terminal states).

## Current State

The setup and communication are fully working, and the first models have been successfully trained. The bots can figure out basic movement and actions inside their specific training zones. However, they are currently overfitting, meaning they struggle when placed in new, untested areas. Future work will focus on making the training data and environments (the gym) better and more varied so the bots learn how to play in any situation.

Note: before observation version 2, the bots did not see their own zone target (the target in their observation came from the manually placed target marker, or was zero), so they could only tell zones apart by the terrain. That alone explains much of the overfitting. Checkpoints trained before version 2 cannot be loaded by the current code.

## Configuration (`Training/training_config.json`)

Both sides read this file. The mod looks for it in `<tModLoader>/ModSources/FirstMod_Sp/Training/` and prints the path it used in the chat; it re-reads it every time a world is loaded.

| Key | Used by | Meaning |
|---|---|---|
| `core_settings.num_bots` | both | Number of bots. Must be equal on both sides (checked at connect). The CLI `--num-bots` and the dashboard slider override the Python side only. |
| `core_settings.port` | both | TCP port. |
| `core_settings.max_steps_per_zone` | mod | Decisions before an episode times out. |
| `core_settings.seed` | Python | Seed for Python, NumPy, PyTorch, PPO and zone sampling (`null` = random). |
| `observation_settings.frame_skip` | mod | Game ticks per decision: each action is held for exactly this many ticks. |
| `observation_settings.frame_stack` | Python | Number of stacked observations (1 = off). Changing it needs a new model. |
| `training_hyperparameters.*` | Python | PPO settings. `batch_size` is the PPO minibatch size, not the number of bots. Loading a checkpoint keeps the hyperparameters it was saved with. |
| `reward_settings.*` | Python | Win/death/timeout rewards, progress weight, and the per-step cap on progress. |
| `path_settings.max_jump_tiles` | mod | How high above the ground the path search lets a bot go. The default 14 is an estimate (base jump about 6.5 tiles plus the Tsunami in a Bottle jump about 8); measure it in game and adjust. |
| `path_settings.field_radius_tiles`, `off_path_penalty_tiles` | mod | Size of the path search around each target, and the penalty added to the straight-line distance when the bot is outside it or cut off from the target. |
| `zone_settings.*` | Python | Difficulty range, performance-weighted sampling, and `zone_database_path` (by default the mod's `TrainingZones/zones_database.json`, which is this repository's folder when it is cloned into `ModSources`). |
| `logging.debug` | both | Per-episode chat messages in game and console output in Python. Keep off with many bots. |

Zone training statistics are written to `Training/logs/zone_stats.json` (the zone database itself is only written by the mod). Checkpoints go to `Training/models/` and TensorBoard logs (including `rollout/success_rate` and `episodes/*` outcome counts) to `Training/logs/`, whatever the current directory is.

## Getting Started

1. Place this repository in your tModLoader `ModSources` directory.
2. Build the mod using the tModLoader game menu and enable it.
3. In the `Training` folder, install the required packages:
   ```bash
   pip install -r requirements.txt
   ```
4. Start the training server or the dashboard:
   ```bash
   python train_terraria.py --dashboard
   ```
   or train from the command line: `python train_terraria.py --num-bots 16`.
5. Load a Terraria world with the mod enabled to establish the connection, then spawn `num_bots` bots with the Bot Spawner (Ctrl + click) to begin training.

## Tests

The Python side is tested without the game: `Training/tests/fake_terraria.py` is a stand-in for the mod that speaks the real protocol.

```bash
pip install -r Training/requirements.txt
pytest Training/tests
```

The tests also run on every push and pull request (GitHub Actions, `.github/workflows/python-tests.yml`). The C# mod has no automated tests; build it in tModLoader.
