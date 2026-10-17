"""Paths and defaults. Every path can be overridden with an environment variable."""

import os
from pathlib import Path

MOD_NAME = "FirstMod_Sp"

# tModLoader install (Steam).
TML_DIR = Path(os.environ.get("TML_DIR", r"K:\SteamLibrary\steamapps\common\tModLoader"))
DOTNET = TML_DIR / "dotnet" / "dotnet.exe"

# The mod's source folder (this repository).
MOD_SOURCE = Path(os.environ.get("TML_MOD_SOURCE", Path(__file__).resolve().parents[2]))
PERSONAL_SAVE_DIR = MOD_SOURCE.parents[1]  # ...\Terraria\tModLoader

# Isolated save directory for automated runs: only this mod, training worlds, the "Automation" character.
SAVE_DIR = Path(os.environ.get("TML_TRAIN_SAVE_DIR", PERSONAL_SAVE_DIR.parent / "tModLoader-Train"))

# Builds go to a staging save directory that no game loads: a running game keeps its .tmod open, and tModLoader's
# build then fails with TML003. The launcher copies the staged .tmod into MODS_DIR.
BUILD_STAGE_DIR = SAVE_DIR / "BuildStage"
BUILD_OUTPUT = BUILD_STAGE_DIR / "Mods" / f"{MOD_NAME}.tmod"
WORLDS_DIR = SAVE_DIR / "Worlds"
MODS_DIR = SAVE_DIR / "Mods"
AUTOMATION_DIR = SAVE_DIR / "Automation"
LOGS_DIR = AUTOMATION_DIR / "logs"
IMAGES_DIR = AUTOMATION_DIR / "images"

AUTOMATION_PLAYER = "Automation"
DEFAULT_WORLD = "bench_small"
FIRST_BRIDGE_PORT = 7878
