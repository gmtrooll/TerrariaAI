"""Set up a fresh Windows machine to run this project: build the mod, launch the game, watch and evaluate the trained
models, and let agents use the Terraria MCP. Training needs an NVIDIA GPU; everything else runs on the CPU.

Before running it (see docs/Laptop Setup.md):
  - Steam with tModLoader installed and started once;
  - this repository cloned to Documents/My Games/Terraria/tModLoader/ModSources/FirstMod_Sp;
  - Python 3.12 and the .NET 8 SDK.

    python tools/setup_machine.py            # everything, then a short evaluation of navigate_v4 as a check
    python tools/setup_machine.py --no-check # skip the check
Safe to run again: existing worlds are kept unless they differ from the repository's (then --force replaces them).
"""

import argparse
import hashlib
import shutil
import subprocess
import sys
import venv
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools"))
from terraria_mcp import config  # noqa: E402

OK, WARN, FAIL = "  ok  ", " warn ", " FAIL "


def say(tag, text):
    print(f"[{tag}] {text}", flush=True)


def run(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, **kw)


def md5(path: Path) -> str:
    return hashlib.md5(path.read_bytes()).hexdigest()


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--force", action="store_true", help="replace training worlds that differ from the repository's")
    p.add_argument("--no-check", action="store_true", help="skip the final evaluation")
    args = p.parse_args()
    problems = 0

    # 1. Python and the repository location
    if sys.version_info < (3, 12):
        say(FAIL, f"Python {sys.version.split()[0]}: use Python 3.12 or newer")
        return 1
    say(OK, f"Python {sys.version.split()[0]}")
    if REPO.parent.name != "ModSources":
        say(FAIL, f"the repository is at {REPO}; tModLoader builds mods only from .../tModLoader/ModSources/FirstMod_Sp")
        return 1
    say(OK, f"repository in {REPO.parent}")

    # 2. tModLoader and the build tools
    if not (config.TML_DIR / "tModLoader.dll").exists():
        say(FAIL, f"tModLoader not found (looked in Steam's libraries; got {config.TML_DIR}). Install it from Steam and start "
                  "it once, or set TML_DIR to its folder")
        return 1
    say(OK, f"tModLoader at {config.TML_DIR}")
    targets = REPO.parent / "tModLoader.targets"
    want = f'<Project ToolsVersion="14.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">\n  <Import Project="{config.TML_DIR / "tMLMod.targets"}" />\n</Project>'
    if not targets.exists() or str(config.TML_DIR) not in targets.read_text(errors="replace"):
        targets.write_text(want)
        say(OK, f"wrote {targets}")
    else:
        say(OK, f"{targets.name} points at this tModLoader")
    sdks = run(["dotnet", "--list-sdks"]) if shutil.which("dotnet") else None
    if not sdks or not any(line.startswith("8.") for line in sdks.stdout.splitlines()):
        say(FAIL, "the .NET 8 SDK is missing: winget install Microsoft.DotNet.SDK.8 (then open a new terminal)")
        return 1
    say(OK, ".NET 8 SDK")

    # 3. Training save folder and worlds (the scenario sets and template vaults belong to these exact worlds)
    config.WORLDS_DIR.mkdir(parents=True, exist_ok=True)
    copied = kept = differ = 0
    for src in sorted((REPO / "worlds").glob("*.*wld")):
        dst = config.WORLDS_DIR / src.name
        if dst.exists() and md5(dst) == md5(src):
            kept += 1
        elif dst.exists() and not args.force:
            differ += 1
            say(WARN, f"{dst.name} differs from the repository's copy (kept; --force replaces it)")
        else:
            shutil.copy2(src, dst)
            copied += 1
    say(OK, f"worlds in {config.WORLDS_DIR}: {copied} copied, {kept} already there" + (f", {differ} differ" if differ else ""))
    problems += differ > 0

    # The shipped models are in Git LFS: a clone without LFS has small pointer files instead
    pointers = [m for m in (REPO / "results" / "training").glob("*/model.pt") if m.stat().st_size < 1024]
    if pointers:
        r = run(["git", "lfs", "pull"], cwd=REPO)
        still = [m for m in pointers if m.stat().st_size < 1024]
        say(OK if not still else FAIL, f"models fetched with git lfs pull ({len(pointers)} files)" if not still
            else "model files are Git LFS pointers and `git lfs pull` failed: install Git LFS (git lfs install), then run it")
        problems += bool(still)
    else:
        say(OK, "model files present")

    # 4. Python packages: the MCP server's own environment, and the training/evaluation packages
    venv_dir = REPO / "tools" / ".venv"
    py = venv_dir / "Scripts" / "python.exe"
    if not py.exists():
        say(OK, "creating tools/.venv for the MCP server")
        venv.create(venv_dir, with_pip=True)
    r = run([str(py), "-m", "pip", "install", "-q", "-r", str(REPO / "tools" / "requirements-lock.txt")])
    say(OK if r.returncode == 0 else FAIL, "MCP server packages" + ("" if r.returncode == 0 else ": " + r.stderr[-400:]))
    problems += r.returncode != 0
    has_gpu = shutil.which("nvidia-smi") is not None and run(["nvidia-smi"]).returncode == 0
    have_torch = run([sys.executable, "-c", "import torch"]).returncode == 0
    if not have_torch:
        if has_gpu:
            say(WARN, "NVIDIA GPU found: install the CUDA build of torch first (https://pytorch.org/get-started/locally/), "
                      "then run this again")
        r = run([sys.executable, "-m", "pip", "install", "-q", "torch"])  # PyPI's Windows wheel is the CPU build
        say(OK if r.returncode == 0 else FAIL, "torch (CPU build: watching and evaluating, no training)")
        problems += r.returncode != 0
    pkgs = ["stable-baselines3", "gymnasium", "numpy", "tensorboard", "pytest", "matplotlib"]
    r = run([sys.executable, "-m", "pip", "install", "-q"] + pkgs)
    say(OK if r.returncode == 0 else FAIL, "training and evaluation packages" + ("" if r.returncode == 0 else ": " + r.stderr[-400:]))
    problems += r.returncode != 0
    cuda = run([sys.executable, "-c", "import torch; print(torch.cuda.is_available())"]).stdout.strip() == "True"
    say(OK, "torch sees a CUDA GPU: training possible" if cuda else "no CUDA GPU: models run on the CPU (watch, evaluate); training is too slow")

    # 5. Build the mod and install it into the training save folder
    from terraria_mcp.game import GameManager
    report = GameManager().build()
    if not report["ok"]:
        say(FAIL, f"mod build failed: {report.get('errors') or report.get('output')}")
        return 1
    say(OK, f"mod built and installed ({report.get('installed')})")

    # 6. Check: a short held-out evaluation of the best model (launches two headless servers)
    if not args.no_check:
        say(OK, "checking: navigate_v4 on 32 held-out scenarios (about 2 minutes)")
        r = subprocess.run([sys.executable, "-m", "navigate.evaluate", "--run", "navigate_v4", "--per-band", "4", "--tag", "setup_check",
                            "--bots", "8", "--port", "5795"], cwd=REPO / "Training", capture_output=True, text=True)
        line = next((l for l in r.stdout.splitlines() if '"success"' in l), "")
        say(OK if r.returncode == 0 else FAIL, f"evaluation {'ran' if r.returncode == 0 else 'failed'} {line.strip()}" + ("" if r.returncode == 0 else "\n" + r.stderr[-800:]))
        problems += r.returncode != 0

    print()
    if problems:
        say(WARN, f"done with {problems} problem(s) above")
    else:
        say(OK, "done. Watch the best model: cd Training; python -m navigate.watch --run navigate_v4   (Steam must be running)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
