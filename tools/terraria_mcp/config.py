"""Paths and defaults. Every path can be overridden with an environment variable."""

import os
import re
from pathlib import Path

MOD_NAME = "FirstMod_Sp"


def _find_tmodloader() -> Path:
    """The tModLoader install: in any Steam library listed in Steam's libraryfolders.vdf (Steam's own folder from
    the registry first), else this PC's library."""
    roots = []
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            roots.append(Path(winreg.QueryValueEx(key, "SteamPath")[0]))
    except OSError:
        pass
    roots += [Path(r"C:\Program Files (x86)\Steam"), Path(r"C:\Program Files\Steam")]
    libraries = []
    for root in roots:
        vdf = root / "steamapps" / "libraryfolders.vdf"
        if vdf.exists():
            libraries += [Path(p.replace("\\\\", "\\")) for p in re.findall(r'"path"\s+"([^"]+)"', vdf.read_text(errors="replace"))]
        libraries.append(root)
    for library in libraries:
        candidate = library / "steamapps" / "common" / "tModLoader"
        if (candidate / "tModLoader.dll").exists():
            return candidate
    return Path(r"K:\SteamLibrary\steamapps\common\tModLoader")


# tModLoader install (Steam): TML_DIR overrides the search.
TML_DIR = Path(os.environ["TML_DIR"]) if os.environ.get("TML_DIR") else _find_tmodloader()
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
