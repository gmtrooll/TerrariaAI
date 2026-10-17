# Throughput and Automation Plan

Oct 17, 2026

This plan comes before the agent architecture ([Terraria Agent Architecture and Plan](Terraria%20Agent%20Architecture%20and%20Plan.md)). It answers two questions:

1. What is the highest training throughput (environment steps per second) on this PC, and later on a university cluster?
2. How much of the develop, launch, train and evaluate loop can agents run without a person at the keyboard?

Every candidate method gets tried and measured. Nothing is ruled out in advance; the numbers decide.

## Machine

| Part | Value |
| --- | --- |
| CPU | Ryzen 7 5700G, 8 cores / 16 threads |
| RAM | 16 GB (a running tModLoader client uses about 1.5 GB) |
| GPU | RTX 3060, 12 GB |
| tModLoader | 1.4.4.9, build 2026.04 stable, installed at `K:\SteamLibrary\steamapps\common\tModLoader` |

## What is already known

- `dotnet build` in the mod folder builds the `.tmod` without the game menu (`tMLMod.targets` runs `tModLoader.dll -build`). tModLoader refuses to build while the game is open.
- The dedicated server starts from the command line: `-server -world <path> -modpath <dir> -autoshutdown`.
- Launch arguments found in `tModLoader.dll` that matter here: `-tmlsavedirectory`, `-nosteam`, `-skipselect`, `-videodriver`, `-audiodriver`, `-autocreate`, `-worldname`, `-seed`, `-world`, `-player`, `-host`, `-join`, `-forcepriority`. What each one does exactly is checked in phase 1.
- The current bots are fake players inside a single-player client (`BotManagerSystem`): a free `Main.player` slot, `Main.myPlayer` switched to the bot during its update. `PhysicsAccelerator` runs the update loop several times per frame; `BackgroundRunSystem` keeps the game running without focus.
- The earlier multiplayer attempt (`FirstMod_MpDeadend`) could not run bots on a server without a person joining. The cause, found in the decompiled server code:
  - the server loop only calls `Main.Update` while `Netplay.HasClients` is true, so with nobody connected the world is frozen
  - the network thread sets `Main.player[i].active = false` for every slot without a connected client, which switches fake bots off

  Both are fixed in `Common/Automation/ServerBotSupport.cs`. The server also needed three more changes for bots:
  - skip `UpdateBiomes`, which drives screen filters that do not exist on a server
  - give the server a camera matrix (`Main.GameViewMatrix`), which item code reads
  - set `Main.hasFocus`, without which `Player.Update` ignores controls

## Progress

- **Phase 1, Oct 17:** the Terraria MCP works end to end (30 tools, see `tools/README.md`).
  - **Server:** cold start to in-world in about 8 s, bots moving, zero errors, clean exit.
  - **Client:** cold start to in-world in about 20 s, through the auto-created `Automation` character and `-skipselect`, with no Steam and no clicks.
  - **Gate:** 15 of 15 cold start, run and stop cycles passed (10 server, 5 client), each with 4 bots moving, zero game errors and a clean exit. Server ready in 7.5 to 8.5 s, client in 23 to 27 s.
  - **Notes for later phases:**
    - On a client, `Main.DoUpdate` runs more often than the world updates (60 Hz time accumulator). `Main.superFast` makes every call a real tick, which is the client's built-in way past the cap (M3).
    - A first rough number: 600 ticks stepped back to back on a server with 4 bots took 0.3 s, about 2,000 ticks/s.
- **Decompiled game source:** in `Documents/My Games/Terraria/tml-reference/src` (ILSpy), outside the mod folder, because tModLoader compiles every `.cs` file inside it.
- **Phase 2, Oct 17:** the harness exists (`bench`, `physics_check`, `profile`; see `tools/README.md`). What it found:
  - **A thrown exception on every bot tick.** The mod's debug keybind (`GridExtractionPlayer.ProcessTriggers`) threw a `KeyNotFoundException` on every bot tick on a server. tModLoader logs every exception as it is thrown, stack trace and all, and that cost about 75% of the game thread. Found with the profiler and fixed.
  - **Two more server-only crashes on bot respawn.** A far respawn takes the local player's camera-jump branch, which calls `Lighting.Clear` and `ForceUpdateBiomes`. Both crashed and aborted the respawn. Fixed in `ServerBotSupport`.
  - **Bit-identical physics.** Same start positions, same actions, enemies cleared and spawns blocked: the trajectories match exactly between server runs, client runs, and server against client (8 bots, 2,400 ticks). Without isolation, a randomly spawned enemy made one run diverge.
  - **Warmup.** The first ~5 s after launch run 2–3× slower (JIT and detour compilation), so warmup ends after 3,000 ticks and at least 5 s.
  - **The bots run the full local-player path.** That includes reading the keyboard, the inventory UI and visual-only work. A vanilla server's remote-player path is about 5× cheaper, but it skips gameplay that runs only for the local player: fall-damage tracking, buff expiry, pressure plates, dash damage. M4 therefore tests "lean" bots: the local-player path minus the parts that only serve a human (`Common/Bots/BotLeanUpdate.cs`).
  - **No network-thread spin.** A paused server uses 0.04 cores, so its network thread costs nothing.

## Existing tools reviewed

| Project | What it is | Use for us |
| --- | --- | --- |
| [tmodloader-mcp](https://github.com/musharna/tmodloader-mcp) | MCP server that drives a running tModLoader: launch, file-based commands, screenshots from the back buffer, logs, build, save snapshots | Ideas only. Alpha, tested on one machine, MCP server runs in WSL2 only, and it requires a person to load the world in single-player, which is the exact step we want to remove. Worth borrowing: back-buffer screenshots, `log_since`, save snapshot and restore |
| [Testaria](https://github.com/sparr/terraria-mod-Testaria) | Headless test harness for tModLoader mods: scratch save directory, builds the mod, generates a blank world, starts a server, runs tests tick by tick | The closest thing to our automation layer. It speeds up the server by hooking `Thread.Sleep()`, runs game logic on a server with nobody joined, and generates a deterministic blank world. Linux first; read its source for M3 and M6 |
| [Tairaria](https://github.com/Reisenbug/Tairaria) | LLM-driven Terraria bot: a mod (TerraBlind, about 150 HTTP endpoints, source not public) does perception, pathfinding, building and combat; an LLM plans and replans on failure | Not a tool for us. It is related work for the thesis: an LLM planner over scripted skills in Terraria, the same split as our architecture without RL skills |
| [TerrarAI](https://github.com/dkoleber/TerrarAI) | RL platform for Terraria from 2019 | Too old (pre-1.4) and undocumented; not used |

Decision: build our own Terraria MCP server, modelled on the Roblox Studio and Blender MCPs, with a mod-side bridge. An agent can then build the mod, start the game, act in it, inspect it and test its own code inside the running game. The MCP server and a thin command-line tool (`tctl`) share one Python core, so scripts and the overnight runner use the same functions.

## Phase 1: Terraria MCP (automation backbone)

Goal: an agent develops and tests inside the game the way it does in Roblox Studio or Blender. From a cold start, one tool call gives a running game with N bots; one call rebuilds and reloads the mod after a code change. No clicks.

**Principles:**

- **Structured data first, pictures second.** State as JSON, a tile area as a compact text grid, entity lists. Screenshots only when the look matters. A map rendered from tile data is cheaper than a screenshot and works without graphics.
- **Wait on conditions, not on screenshots.** `wait_until` blocks until a C# condition holds, or times out, instead of polling.
- **Input is injected inside the mod,** not by moving the real mouse. It works with the window minimized, on a server, and for any bot.
- **The MCP is for development, testing and supervision, never for training steps.** RL training keeps its own fast socket; the MCP only starts, watches and evaluates training.

**Isolated training install.** A separate save directory (`-tmlsavedirectory`) holding only this mod and training worlds, so automation never touches personal worlds or other mods. Possibly also a copied tModLoader install, so a Steam update cannot change the game mid-project.

**Tools:**

| Group | Tools |
| --- | --- |
| Session | `launch` (client or server, world, bot count, flags), `stop`, `status`, `restart`, `build_mod`, `rebuild_and_reload` (leave the world, build, reload mods, re-enter, respawn bots), `logs_since` |
| Run code | `execute_csharp`: compile a C# snippet at runtime against the loaded game and mod, run it on the game thread, return its result and output. The equivalent of Roblox's `execute_luau` and Blender's `execute_blender_code`. `run_command` for the mod's registered commands |
| Observe | `get_state` (player, bots, world flags), `get_tiles` (region as a text grid), `get_entities`, `find` (tile, NPC or item), `screenshot` (region and scale, from the back buffer), `render_map` (region from tile data, with bot paths drawn on top) |
| Act | `input` (keys, mouse buttons and aim for the local player or any bot, with hold durations), `spawn_bots`, `despawn_bots`, `spawn_npc`, `give`, `teleport`, `set_tile`, `set_time`, `world_snapshot`, `world_restore` |
| Time | `pause`, `resume`, `step` (advance N ticks), `set_speed`, `wait_until` |
| Experiments | `run_bench`, `start_training`, `training_status`, `evaluate_checkpoint`, `results_summary`. Added in phases 2 and 4 |

**Mod side (`Common/Automation/`):**

- **Auto-enter:** read a launch file at the main menu and load the given world, or test whether `-skipselect` already does this.
- **Auto-spawn:** spawn N bots on world load, from the config, in client and server mode.
- **Bridge:** JSON over a local TCP port, separate from the training socket. Commands run on the game thread between ticks.
- **Runtime C# execution:** use the Roslyn compiler that tModLoader already ships to compile snippets into a collectible assembly. Feasibility is checked first, because it is the most valuable tool.
- **Event log:** JSONL per episode and per run.

**Python side (`tools/`):** the MCP server, the shared core, the `tctl` command line and a watchdog that restarts the game after a crash. The MCP server is registered in the project's `.mcp.json`.

**Gate:**

- ten cold start, run and stop cycles in a row without a failure, in client mode, and in server mode if M6 gets that far
- `execute_csharp` runs a snippet that reads and changes game state
- `rebuild_and_reload` takes a code change to running in game in one call

## Phase 2: Benchmark harness

- **Driver:** a random-policy driver in two versions. One runs inside the mod (measures only the game). One runs over the socket from Python (measures game plus transport).
- **Metrics:** decisions per second, game ticks per second, time per tick split into sections (player update, NPC update, observation, socket), RAM per process, CPU use.
- **Output:** one command per configuration, results appended to `results/bench/*.csv`, plus a short summary.
- **Physics check:** the same recorded action sequence played in every mode, comparing bot positions. A speedup that changes physics is rejected or flagged.

**Gate:** the baseline (M1) measured three times, with less than 10% spread between runs.

## Phase 3: Method matrix

Each method has a hypothesis, a measurement and a result row. Methods that change only one factor are measured alone first, then combined.

| ID | Method | Factor | How |
| --- | --- | --- | --- |
| M1 | Client, current code | baseline | Current `BotManagerSystem` with a random policy |
| M2 | Lean mode | ticks/s | Switches, each measured alone: no drawing, lighting, sound, dust and gore, liquids, enemy spawns, random world updates, clock |
| M3 | No tick cap | ticks/s | Client: run updates without waiting for the frame. Server: hook `Thread.Sleep()` as Testaria does |
| M4 | Bots per process | bots | Sweep 1, 4, 16, 64, 128, 250. Find where per-bot cost dominates and what it is spent on |
| M5 | Processes and world size | processes | Sweep 1 to 8 processes, with small blank, small generated and medium worlds. RAM per process decides the ceiling |
| M6 | Dedicated server with fake bots | no graphics | Spike, time-boxed to two days. Spawn from code on world load; check that bots move with the same physics as in the client |
| M7 | Client without drawing | no graphics | Draw hooked off, window minimized; also try `-videodriver dummy` and `-audiodriver dummy` |
| M8 | Learner throughput | the cap | SB3 PPO against a lean custom PPO on synthetic data; grids as bytes instead of floats; TCP against shared memory; batched inference |
| M9a | Game code as a library | ticks/s | Load `tModLoader.dll` in a small custom process without the game loop and call `Player.Update` on an in-memory world. Exact physics, no main loop. Feasibility unknown |
| M9b | Custom simulator | 10 to 100× for movement | Movement-only physics (JAX or C#), validated against the real game with the physics check. Build cost and fidelity are the questions |
| M10 | Best combination | all | The best settings from M2 to M9 together, run for an hour to check stability |

**Order:** M1 → M2 → M3 → M4 → M5 → M7 → M8, then M6 and M9a as spikes, then M9b, then M10. M8 can run in parallel at any time because it does not need the game.

**Gate for the phase:** a results table with every method measured, a chosen configuration for Navigate training, and a measured steps-per-second figure that replaces the estimates in the architecture plan.

## Phase 4: Agent-run experiments

- **Experiment runner:** runs a list of configurations (benchmarks or short training runs) unattended, for example overnight, and writes one report.
- **Visual review without watching:**
  - trajectory images: the terrain around each episode with the bot's path drawn on top
  - frame strips: screenshots at fixed intervals, joined into one image
  - plots of the training curves

  An agent reads all of these as images.
- **Checkpoint evaluation:** every saved model runs a fixed evaluation set and writes success rate, time and trajectory images.
- **Still done by a person:** research decisions, a periodic look at real video to catch behaviour that metrics miss, and anything needing Steam login or first-time setup.

## Cluster (later)

- **Node shape:** the game needs CPU cores and RAM, not GPUs.
- **No display:** a cluster node has none, so the client only runs under a virtual display (Xvfb). That makes M6, M7 and M9 more important there than on the PC.
- **Questions for the admins:** these are listed in the architecture plan.

## Results

Filled in as methods are measured.

Unthrottled (bench steps ticks back to back), `frame_skip` 4, random policy, small world, means of 3 repeats (2 for M5). Raw rows are in `results/bench/bench.csv`.

| ID | Configuration | Decisions/s (total) | Ticks/s per process | µs per bot tick | RAM/process | Physics check | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| M1 | server, 16 bots | 6,628 | 1,657 | 18.5 | 490 MB | identical to client | current bots, after the keybind and respawn fixes |
| M1 | server, 64 bots | 8,483 | 530 | 21.9 | 495 MB | | |
| M1 | server, 128 bots | 8,259 | 258 | 24.6 | 490 MB | | per-bot cost grows with bot count (cache pressure) |
| M1 | client, 64 bots | 7,322 | 458 | 25.2 | 840 MB | identical to server | a client uses 1.7× the RAM |
| M4 | server, 64 lean bots | 9,478 | 592 | 19.0 | 500 MB | identical to M1 | +12%: skips keyboard copy, inventory UI, idle held-item clone, prompts, dyes, biome visuals |
| M4 | server, 128 lean bots | 8,891 | 278 | 22.8 | 510 MB | | +8% |
| M5 | 8 servers × 64 lean bots | **34,871** | 272 | 41.3 | 495 MB | | 4.0× from 8 processes; per-process speed falls as processes share the L3 cache and memory |
