# Laptop Setup

How to run this project on a second Windows machine (written Oct 21 for the presentation laptop). Without an NVIDIA GPU everything works except training: building the mod, launching the game, watching and evaluating the trained models, the hand-made tests, and the Terraria MCP for agents. The models run on the CPU.

## 1. Install

| What | How |
| --- | --- |
| Steam, Terraria and tModLoader | tModLoader is free on Steam but needs Terraria on the same account. Install it and **start it once** (it creates its folders). The version used here is 1.4.4.9, build 2026.08 stable; Steam keeps it current |
| Git, with Git LFS | `winget install Git.Git` (Git for Windows includes Git LFS; the model files are stored with it) |
| Python 3.12 | `winget install Python.Python.3.12` (tick "Add to PATH") |
| .NET 8 SDK | `winget install Microsoft.DotNet.SDK.8` |
| Claude Code (for agents) | as on the PC |

## 2. Get the repository

It must sit in tModLoader's `ModSources` folder, or tModLoader cannot build it:

```powershell
cd "$HOME\Documents\My Games\Terraria\tModLoader\ModSources"
git clone https://github.com/gmtrooll/TerrariaAI.git FirstMod_Sp
cd FirstMod_Sp
```

(If `ModSources` does not exist yet, start tModLoader once and open Workshop → Develop Mods.)

## 3. Run the setup script

```powershell
python tools/setup_machine.py
```

It checks each requirement and says what is missing. It then:

1. finds tModLoader through Steam's library list (or `TML_DIR`);
2. writes `ModSources/tModLoader.targets`;
3. copies the 16 worlds from `worlds/` into the isolated training folder `Documents/My Games/Terraria/tModLoader-Train/Worlds`. These are the exact worlds the scenario sets and template vaults were made in; regenerating them from their seeds would not match;
4. creates `tools/.venv` for the MCP server;
5. installs torch (the CPU build on a machine without NVIDIA) and the evaluation packages;
6. builds and installs the mod;
7. runs a check: `navigate_v4` on 32 held-out scenarios.

It is safe to run again.

## 4. Presenting

All commands from the `Training` folder. **Steam must be running** for anything that opens a game window.

| Show | Command |
| --- | --- |
| The best model on a world it never saw | `python -m navigate.watch --run navigate_v4` |
| Only long routes (60–120 tiles) | `python -m navigate.watch --run navigate_v4 --band 60-120` |
| The bot follows you: walk around with your character | `python -m navigate.watch --run navigate_v4 --mode follow` |
| Routes that bend away from the target | `python -m navigate.watch --run navigate_v4 --set _bend` |
| Movement categories (honey, water, ice, cobwebs, doors…) | `python -m navigate.watch --run navigate_v4 --set _cat` |
| Unseen gear: Bundle of Balloons on routes that need its height | `python -m navigate.watch --run gear3_10m --loadout bundle --set _h59` |
| The same without the gear description, for contrast | `python -m navigate.watch --run nogear2_10m --loadout bundle --set _h59` |
| Another unseen item: Blizzard in a Bottle | `python -m navigate.watch --run gear3_10m --loadout blizzard --set _h14` |
| A score on the spot (headless, 1–2 minutes) | `python -m navigate.evaluate --run navigate_v4 --per-band 16 --tag demo` |
| Hand-made ledge tests, old vs new | `python -m navigate.handmade --run grid_ctrl_10m --repeats 4` then the same with `navigate_v4` |

In the game window: keys 1–9 put the camera on a bot, 0 on your own character. Yellow sparkles mark targets, blue ones route waypoints. The terminal prints each episode's result and the running success rate.

Models in the repository: `navigate_v1`, `navigate_v2`, `navigate_v3`, `navigate_v4` (best), `grid_ctrl_10m` (before the line-of-sight waypoint), `gear3_10m` and `nogear2_10m` (equipment). Loadout names are in `Training/loadouts.json`.

The documents to show: `docs/Navigate Progress.md` (full history), `docs/Movement Equipment.md`, `docs/Terraria Agent Architecture and Plan.md`. The handbook and the supervisor brief are claude.ai artifacts and open in a browser.

## 5. Agents

Open Claude Code in the repository folder. `.mcp.json` registers the Terraria MCP server (from `tools/.venv`), so an agent can build, launch, inspect and test the game as on the PC.

Two things live outside the repository:

- **Claude's project memory** (preferences and project state): copy `C:\Users\User\.claude\projects\c--Users-User-Documents-My-Games-Terraria-tModLoader-ModSources-FirstMod-Sp\memory\` from the PC to the same place on the laptop. If the laptop's user folder has another name, the project folder name changes accordingly: start Claude Code in the repository once, then copy the files into the `memory` folder of the new project folder under `.claude\projects`.
- **The decompiled game source** that agents read before writing game code: copy `Documents\My Games\Terraria\tml-reference\` from the PC, or decompile again: `dotnet tool install -g ilspycmd`, then `ilspycmd -p -o src --nested-directories -r "<tModLoader>\Libraries" "<tModLoader>\tModLoader.dll"` inside a new `tml-reference` folder.

## What does not carry over

- **Training.** It needs an NVIDIA GPU; on the CPU a 10M-step run would take days.
- **The physics movement graphs** (1.2 GB, `Training/movegraphs`, only used by the physics-reward experiment): regenerate with `navigate.movegraph` if ever needed.
- **Checkpoints other than the models listed above** and the run logs: they stay on the PC.
