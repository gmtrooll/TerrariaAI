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
- Trains multiple bots at the same time using vectorized environments (`VecEnv`). It also uses frame stacking (`VecFrameStack`) to give the bots memory of what just happened.
- Includes a live Streamlit dashboard (`dashboard.py`) to track how the training is going, watch model performance, and handle save files.

### Communication Layer (TCP Sockets)

To make sure actions happen instantly without lag, the C# mod and Python script talk over local TCP sockets. The C# client packs the game data (vision grids, stats) into tight binary buffers. The Python server reads this data, runs it through the AI model, and sends back a binary number that tells each bot what buttons to press (like move left, move right, or jump).

## Current State

The setup and communication are fully working, and the first models have been successfully trained. The bots can figure out basic movement and actions inside their specific training zones. However, they are currently overfitting, meaning they struggle when placed in new, untested areas. Future work will focus on making the training data and environments (the gym) better and more varied so the bots learn how to play in any situation.

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
5. Load a Terraria world with the mod enabled to establish the connection and begin training.
