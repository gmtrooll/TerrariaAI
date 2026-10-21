"""Success per loadout for runs evaluated with `evaluate --loadout L --tag gear_L` (navigate/queue_gear.sh), and paired
comparisons of two runs per loadout and pooled over the training and the held-out loadouts.

    python -m navigate.gear_report gear_10m nogear_10m navigate_v3
"""

import argparse
import json
import math
from pathlib import Path

from navigate.env import LOADOUT_ROWS

RESULTS = Path(__file__).resolve().parents[2] / "results" / "training"


def episodes(run, loadout):
    path = RESULTS / run / f"eval_gear_{loadout}.json"
    return {e["id"]: e["win"] for e in json.loads(path.read_text())["episodes"]} if path.exists() else None


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("runs", nargs="+", help="the first two are compared")
    args = p.parse_args()
    rows = sorted(LOADOUT_ROWS.values(), key=lambda r: (r["split"] != "train", r["code"]))
    print(f"{'loadout':26s} {'split':5s} {'jump':>5s} {'n':>4s} " + " ".join(f"{r:>14s}" for r in args.runs) + "   paired (first two)")
    pooled = {"train": [0, 0], "test": [0, 0]}
    for r in rows:
        eps = [episodes(run, r["name"]) for run in args.runs]
        n = len(eps[0]) if eps[0] else 0
        cells = [f"{100 * sum(e.values()) / len(e):13.1f}%" if e else f"{'—':>14s}" for e in eps]
        pair = ""
        if eps[0] and eps[1]:
            ks = [k for k in eps[0] if k in eps[1]]
            a = sum(eps[0][k] and not eps[1][k] for k in ks)
            b = sum(eps[1][k] and not eps[0][k] for k in ks)
            pooled[r["split"]][0] += a
            pooled[r["split"]][1] += b
            pair = f"   only first {a:3d}, only second {b:3d}"
        print(f"{r['name']:26s} {r['split']:5s} {r['jump_tiles']:5d} {n:4d} " + " ".join(cells) + pair)
    for split, (a, b) in pooled.items():
        z = (a - b) / math.sqrt(a + b) if a + b else 0.0
        print(f"pooled {split}: only {args.runs[0]} {a}, only {args.runs[1]} {b}, z = {z:.2f} (positive: {args.runs[0]} better)")


if __name__ == "__main__":
    main()
