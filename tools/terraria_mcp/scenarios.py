"""Navigate scenario sets: generate worlds and start/target pairs in them, saved as JSON Lines in
Training/scenarios/<world>.jsonl (one scenario per line). Training worlds and held-out evaluation worlds are
separate, so evaluation measures generalization to terrain never trained on.

    python -m terraria_mcp.scenarios --worlds train_01 train_02 eval_01 --seeds 101 102 901 --per-layer 400
"""

import argparse
import json
from pathlib import Path

from . import config
from .game import GameManager

SCENARIO_DIR = config.MOD_SOURCE / "Training" / "scenarios"
# Distance bands (path tiles) for the curriculum; each band gets an equal share of a world's scenarios.
BANDS = [(5, 15), (15, 30), (30, 60), (60, 120)]
LAYERS = ["surface", "underground", "cavern"]


def generate_world_set(world: str, seed: int, per_layer: int = 400, max_jump: int = 13, size: str = "small",
                       games: GameManager = None) -> dict:
    games = games or GameManager()
    games.create_world(world, size=size, seed=str(seed))
    name = f"scen_{world}"
    games.launch("server", world=world, name=name)
    rows = []
    try:
        for layer in LAYERS:
            for band_index, (lo, hi) in enumerate(BANDS):
                result = games.call(name, "generate_scenarios", timeout=600, count=per_layer // len(BANDS),
                                    min_distance=lo, max_distance=hi, layer=layer, seed=seed * 100 + band_index,
                                    max_jump=max_jump, max_air_gap=5, radius=max(hi + 10, 40))
                for s in result["scenarios"]:
                    s.pop("path", None)
                    s.update(world=world, world_seed=seed, band=f"{lo}-{hi}", max_jump=max_jump, max_air_gap=5)
                    rows.append(s)
    finally:
        games.stop(name)
    SCENARIO_DIR.mkdir(parents=True, exist_ok=True)
    path = SCENARIO_DIR / f"{world}.jsonl"
    path.write_text("".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")
    counts = {}
    for r in rows:
        counts[(r["layer"], r["band"])] = counts.get((r["layer"], r["band"]), 0) + 1
    return {"world": world, "seed": seed, "scenarios": len(rows), "file": str(path),
            "by_layer_band": {f"{k[0]} {k[1]}": v for k, v in sorted(counts.items())}}


def load(world: str) -> list:
    path = SCENARIO_DIR / f"{world}.jsonl"
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--worlds", nargs="+", required=True)
    parser.add_argument("--seeds", nargs="+", type=int, required=True)
    parser.add_argument("--per-layer", type=int, default=400)
    parser.add_argument("--max-jump", type=int, default=13)
    args = parser.parse_args()
    games = GameManager()
    try:
        for world, seed in zip(args.worlds, args.seeds):
            print(json.dumps(generate_world_set(world, seed, args.per_layer, args.max_jump, games=games)), flush=True)
    finally:
        games.stop_all()


if __name__ == "__main__":
    main()
