"""A scenario set for an existing world, e.g. the no-extra-jump variant (_j6), without regenerating the world
(terraria_mcp.scenarios.generate_world_set creates the world first). Same layout as the main sets: per layer and band.

    python -m navigate.scenario_set --worlds train_07 train_08 --suffix _j6 --max-jump 6
"""

import argparse
import json
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from terraria_mcp.game import GameManager  # noqa: E402
from terraria_mcp.scenarios import BANDS, LAYERS, SCENARIO_DIR  # noqa: E402


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--worlds", nargs="+", required=True)
    p.add_argument("--suffix", required=True)
    p.add_argument("--max-jump", type=int, default=13)
    p.add_argument("--per-layer", type=int, default=400)
    p.add_argument("--seed", type=int, default=600)
    args = p.parse_args()
    games = GameManager()
    for wi, world in enumerate(args.worlds):
        name = f"set_{world}"
        games.launch("server", world=world, name=name, bridge_port=7899)
        rows = []
        try:
            for layer in LAYERS:
                for bi, (lo, hi) in enumerate(BANDS):
                    r = games.call(name, "generate_scenarios", timeout=900, count=args.per_layer // len(BANDS), min_distance=lo,
                                   max_distance=hi, layer=layer, seed=(args.seed + wi) * 100 + bi, max_jump=args.max_jump,
                                   max_air_gap=5, radius=max(hi + 10, 40))
                    for s in r["scenarios"]:
                        s.pop("path", None)
                        s.update(world=world, band=f"{lo}-{hi}", max_jump=args.max_jump, max_air_gap=5)
                        rows.append(s)
        finally:
            games.stop(name)
        path = SCENARIO_DIR / f"{world}{args.suffix}.jsonl"
        path.write_text("".join(json.dumps(s) + "\n" for s in rows), encoding="utf-8")
        print(f"wrote {path}: {len(rows)} scenarios {dict(Counter(s['band'] for s in rows))}", flush=True)


if __name__ == "__main__":
    main()
