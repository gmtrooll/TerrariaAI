"""Learning curves of several runs on one figure.

    python -m navigate.plot parity_sb3 parity_custom --out parity.png

Panels: win rate per iteration (smoothed), number of open curriculum bands, win rate of the 5-15 and 15-30 bands,
throughput. x axis: environment steps (or --x seconds for wall-clock time).
"""

import argparse
import csv
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt  # noqa: E402

RESULTS = Path(__file__).resolve().parents[2] / "results" / "training"


def load(run: str) -> list:
    with open(RESULTS / run / "progress.csv", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def column(rows, key):
    out = []
    for r in rows:
        v = r.get(key)
        out.append(float(v) if v not in (None, "") else float("nan"))
    return out


def smooth(values, k=10):
    out, window = [], []
    for v in values:
        if v == v:  # not NaN
            window.append(v)
            if len(window) > k:
                window.pop(0)
        out.append(sum(window) / len(window) if window else float("nan"))
    return out


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("runs", nargs="+")
    p.add_argument("--out", required=True)
    p.add_argument("--x", choices=["timesteps", "seconds"], default="timesteps")
    args = p.parse_args()

    fig, axes = plt.subplots(2, 2, figsize=(12, 8))
    panels = [("win_rate", "win rate (all episodes, smoothed)"), ("open_bands", "open curriculum bands"),
              ("win_15-30", "win rate, 15-30 band (last 200)"), ("steps_per_s", "steps per second")]
    for run in args.runs:
        rows = load(run)
        x = column(rows, args.x)
        for ax, (key, title) in zip(axes.flat, panels):
            y = column(rows, key)
            ax.plot(x, smooth(y) if key == "win_rate" else y, label=run)
            ax.set_title(title)
            ax.set_xlabel(args.x)
            ax.grid(alpha=0.3)
    axes[0, 0].legend()
    fig.tight_layout()
    out = RESULTS / args.out
    fig.savefig(out, dpi=110)
    print(out)


if __name__ == "__main__":
    main()
