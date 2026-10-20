"""A larger test set of bending routes (route at least --min-ratio times the straight-line distance) from existing
worlds, sampled with new seeds by the mod's generator (the world itself is not regenerated). Scenarios already in the
world's main set are left out.

    python -m navigate.bend_set --worlds eval_01 eval_02 --per-band 150
Writes Training/scenarios/<world>_bend.jsonl; evaluate with `navigate.evaluate --set _bend`.
"""

import argparse
import json
import math
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from terraria_mcp.game import GameManager  # noqa: E402
from terraria_mcp.scenarios import BANDS, LAYERS, SCENARIO_DIR, load  # noqa: E402


def ratio(s):
    return s["path_distance"] / max(1.0, math.hypot(s["target_x"] - s["start_x"], s["target_y"] - s["start_y"]))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--worlds", nargs="+", default=["eval_01", "eval_02"])
    p.add_argument("--per-band", type=int, default=150, help="bending scenarios to keep per band per world")
    p.add_argument("--min-ratio", type=float, default=1.7)
    p.add_argument("--seed", type=int, default=7000)
    args = p.parse_args()
    games = GameManager()
    for world in args.worlds:
        existing = {(s["start_x"], s["start_y"], s["target_x"], s["target_y"]) for s in load(world)}
        name = f"bend_{world}"
        games.launch("server", world=world, name=name, bridge_port=7897)
        kept = {f"{lo}-{hi}": [] for lo, hi in BANDS}
        seen = set()
        try:
            for round_ in range(12):  # sample until every band has enough, at most 12 rounds
                if all(len(v) >= args.per_band for v in kept.values()):
                    break
                for li, layer in enumerate(LAYERS):
                    for bi, (lo, hi) in enumerate(BANDS):
                        band = f"{lo}-{hi}"
                        if len(kept[band]) >= args.per_band:
                            continue
                        r = games.call(name, "generate_scenarios", timeout=900, count=200, min_distance=lo, max_distance=hi,
                                       layer=layer, seed=args.seed + round_ * 100 + li * 10 + bi, max_jump=13, max_air_gap=5,
                                       radius=max(hi + 10, 40), per_target=3)
                        for s in r["scenarios"]:
                            s.pop("path", None)
                            key = (s["start_x"], s["start_y"], s["target_x"], s["target_y"])
                            if key in existing or key in seen or ratio(s) < args.min_ratio:
                                continue
                            seen.add(key)
                            s.update(world=world, band=band, max_jump=13, max_air_gap=5, set="bend")
                            kept[band].append(s)
                print(f"{world} round {round_ + 1}: " + ", ".join(f"{b} {len(v)}" for b, v in kept.items()), flush=True)
        finally:
            games.stop(name)
        rows = [s for v in kept.values() for s in v[:args.per_band]]
        path = SCENARIO_DIR / f"{world}_bend.jsonl"
        path.write_text("".join(json.dumps(s) + "\n" for s in rows), encoding="utf-8")
        print(f"wrote {path}: {len(rows)} scenarios; layers {dict(Counter(s['layer'] for s in rows))}", flush=True)


if __name__ == "__main__":
    main()
