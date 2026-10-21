# Terraria Agent

A thesis project: an agent that carries out Terraria tasks given in plain words, built as a planner over skills trained with reinforcement learning in the real game (tModLoader 1.4.4). The first skill, **Navigate**, moves the player to any target on worlds it has never seen; it is trained with a custom PPO on headless game servers.

The plan: [docs/Terraria Agent Architecture and Plan.md](docs/Terraria%20Agent%20Architecture%20and%20Plan.md). The full history of experiments and results: [docs/Navigate Progress.md](docs/Navigate%20Progress.md).

## Where it stands (21 October 2026)

| | Success on held-out worlds |
| --- | --- |
| `navigate_v4`, with Tsunami in a Bottle (1,024 scenarios, mean of 3 runs) | **97.1%** |
| `navigate_v4`, without an extra jump | 88.4% |
| Best scripted bot (walk to the target, jump when stuck) | 54% |

`navigate_v4` trains from scratch in 2.5 hours on one PC (30M decisions, about 3,400 per second) and is at 95% after 51 minutes. First equipment results: describing gear to the policy by its measured abilities lets it use items it never trained with (Bundle of Balloons: 92% on routes that need its height), provided the training gear covers the range of abilities.

## Repository

| Path | What |
| --- | --- |
| `Common/Automation/` | Terraria MCP bridge (JSON over TCP), runtime C# execution, fixes that let bots run on a headless dedicated server |
| `Common/Env/` | the Navigate training environment (protocol "TNV1"), loadouts, world integrity |
| `Common/Observation/` | tile classes and the observation builder (grids, self state, capabilities, goal and waypoints) |
| `Common/Systems/PathDistanceField.cs` | the grid route planner |
| `Common/Planning/` | physics movement sampler and graph |
| `Common/Scenarios/` | scenario and movement-category generators |
| `Training/navigate/` | custom PPO (`train.py`, `ppo.py`, `model.py`), environment client (`env.py`), `evaluate.py`, `watch.py`, test-set and measurement tools |
| `Training/experiments/` | the queue scripts that ran each experiment (flags, seeds and order), as a record |
| `Training/tests/` | checks of code and data that need neither the game nor torch (run on every push) |
| `Training/scenarios/` | scenario sets per world (`.jsonl`) |
| `Training/loadouts.json` | equipment loadouts: items, measured abilities, planner jump height |
| `worlds/` | the 16 training and test worlds the scenario sets belong to |
| `results/training/<run>/` | config, training log and evaluations per run; model files (Git LFS) for the runs listed below |
| `tools/terraria_mcp/` | MCP server, launcher, bridge client, benchmarks, profiler; `tools/setup_machine.py` |
| `docs/` | design documents and progress logs |

Models in the repository (Git LFS): `navigate_v4` (the best), `grid_ctrl_10m` (before the line-of-sight waypoint, for the ledge comparison), `gear3_10m` and `nogear2_10m` (equipment, with and without the gear description). The other runs keep their configs, training logs and evaluations; their model files stay on the training PC.

**The first version** of the project (December 2025: hand-placed training zones, Stable-Baselines3, a streamlit dashboard) was removed on 21 October 2026; it is kept under the git tag `legacy-zones-pipeline` (`git checkout legacy-zones-pipeline`).

## Setup

Windows, with Steam (Terraria + tModLoader), Git with Git LFS (included in Git for Windows), Python 3.12 and the .NET 8 SDK. Clone into tModLoader's `ModSources` folder, then run the setup script:

```powershell
cd "$HOME\Documents\My Games\Terraria\tModLoader\ModSources"
git clone https://github.com/gmtrooll/TerrariaAI.git FirstMod_Sp
cd FirstMod_Sp
python tools/setup_machine.py
```

It finds tModLoader through Steam, copies the worlds into an isolated save folder (`Documents/My Games/Terraria/tModLoader-Train`, so personal worlds and mods are never touched), installs the Python packages, builds the mod and runs a short evaluation as a check. Training needs an NVIDIA GPU; everything else runs on the CPU. Details: [docs/Laptop Setup.md](docs/Laptop%20Setup.md).

## Use

From the `Training` folder. Anything that opens a game window needs Steam running.

```bash
# watch the best model on a world it never saw (keys 1-9: camera on a bot)
python -m navigate.watch --run navigate_v4
python -m navigate.watch --run navigate_v4 --mode follow                    # it follows your character
python -m navigate.watch --run gear3_10m --loadout bundle --set _h59       # unseen gear, routes that need it

# evaluate on the held-out worlds (headless; sampled actions, as in training)
python -m navigate.evaluate --run navigate_v4 --per-band 128 --tag big     # the 1,024-scenario set
python -m navigate.evaluate --run navigate_v4 --set _bend --tag bend       # routes that bend away from the target
python -m navigate.handmade --run navigate_v4 --repeats 8                  # hand-made ledge and pocket tests

# train (navigate_v4's settings: 12 worlds x 16 bots, 30M steps, about 2.5 hours on an RTX 3060)
python -m navigate.train --run my_run --worlds train_01 train_02 train_03 train_04 train_05 train_06 \
    train_07 train_08 train_09 train_10 train_11 train_12 --bots 16 --steps 30000000 --bf16 --overlap \
    --micro-batch 2048 --target-kl 0.03 --recurrent --obs grid=fovea,visibility=exposed,near=1,los=1 \
    --detour 0.3 --loadouts tsunami,none --categories 0.3
```

Comparing two models: evaluate both on the same fixed scenarios and count the scenarios only one of them solves (McNemar's test); single-run differences under about a point are noise.

**Agents.** Open Claude Code in this folder: `.mcp.json` registers the Terraria MCP server, so an agent can build the mod, launch a client or a headless server, run C# inside the game, read state and tiles, inject input for any bot, step ticks and run benchmarks. Tool list: [tools/README.md](tools/README.md).

## How Navigate works

- **Game side.** Fake-player bots, 16–64 per headless server, several servers in parallel; physics is bit-identical to the client. Each server talks to the learner over its own socket: every 4 ticks it sends every bot's observation and episode events and receives actions. Episodes start anywhere a scenario says.
- **Scenarios.** Start/target pairs generated from the planner's own search: 1,200 per world over 3 depth layers and 4 route-length bands, on 12 training and 2 held-out worlds, plus sets for bending routes, movement categories, obstacle templates and routes that need more jump height.
- **Planner.** A cheapest-path search on the tile grid, matched to the game's rules (2-tile-wide body, measured jump heights, liquids, cobwebs, honey, fall damage, doors). It sets the progress reward and gives the policy its waypoints.
- **Observation.** A 41 × 41 tile grid and a 30 × 17 overview of the player's screen (only tiles a player could see), self state, capabilities, and the goal with three waypoints (far with line of sight, near). Design: [docs/Observation Design.md](docs/Observation%20Design.md).
- **Actions.** Three heads every 4 ticks: left/none/right, up/none/down, jump. [docs/Action Design.md](docs/Action%20Design.md).
- **Learner.** PPO in PyTorch with overlapped collection, bf16, a GRU memory and a target-KL stop. [docs/Custom Learner Design.md](docs/Custom%20Learner%20Design.md).
- **Reward.** +20 at the target, −5 for dying, −1 for timing out, +0.5 per tile of route progress, −5 per full life of damage.

## Documents

| Document | Contents |
| --- | --- |
| [Terraria Agent Architecture and Plan](docs/Terraria%20Agent%20Architecture%20and%20Plan.md) | goal, the five layers, the skills, roadmap, evaluation plan, related work |
| [Throughput and Automation Plan](docs/Throughput%20and%20Automation%20Plan.md) | headless servers, the MCP, benchmarks, learner speed |
| [Navigate Progress](docs/Navigate%20Progress.md) | every experiment since 18 October, newest last |
| [Overnight Log](docs/Overnight%20Log.md) | the first Navigate experiments |
| [Observation](docs/Observation%20Design.md), [Action](docs/Action%20Design.md), [Custom Learner](docs/Custom%20Learner%20Design.md), [Physics Planner](docs/Physics%20Planner%20Design.md) design | the design decisions, from the decompiled game code |
| [Movement Hazards](docs/Movement%20Hazards.md), [Movement Equipment](docs/Movement%20Equipment.md) | everything in the game that changes how a player moves, with code references |
| [Laptop Setup](docs/Laptop%20Setup.md) | setting up a second machine; presenting |

## Tests

`pytest Training/tests` (GitHub Actions, every push) checks what can be checked without the game: every Python file compiles, the loadout table is consistent, every scenario set has its world and matching reachability and height sets, every shipped model has its config. The behaviour is checked inside the running game: the physics check (server against client, bit-identical), benchmarks, and the held-out evaluations above.
