"""Markdown table of finished runs: held-out success (eval.json) by band and for caverns, last training win rate and
median throughput.

    python -m navigate.summary fast_fovea_exposed no_waypoint stride2
"""

import csv
import json
import statistics
import sys
from pathlib import Path

RESULTS = Path(__file__).resolve().parents[2] / "results" / "training"
BANDS = ["5-15", "15-30", "30-60", "60-120"]


def pct(x):
    return "–" if x is None else f"{round(100 * x)}%"


def row(run: str) -> str:
    d = RESULTS / run
    config = json.loads((d / "config.json").read_text())
    differs = []
    if config.get("obs") != "grid=fovea,visibility=exposed":
        differs.append(config.get("obs"))
    if config.get("stride", 1) != 1:
        differs.append(f"stride {config['stride']}")
    ev = json.loads((d / "eval.json").read_text()) if (d / "eval.json").exists() else None
    rows = list(csv.DictReader(open(d / "progress.csv", encoding="utf-8")))
    sps = statistics.median(float(r["steps_per_s"]) for r in rows[5:]) if len(rows) > 5 else None
    last = float(rows[-1]["win_rate"]) if rows and rows[-1].get("win_rate") else None
    cells = [f"`{run}`", ", ".join(differs) or "reference"]
    if ev:
        cells += [f"**{pct(ev['overall']['success'])}**"] + [pct(ev["by_band"].get(b, {}).get("success")) for b in BANDS]
        cells.append(pct(ev["by_layer"].get("cavern", {}).get("success")))
    else:
        cells += ["–"] * 6
    cells += [pct(last), f"{sps:,.0f}" if sps else "–"]
    return "| " + " | ".join(cells) + " |"


def main():
    print("| Run | What differs | Held-out overall | 5–15 | 15–30 | 30–60 | 60–120 | Cavern | Train win (last iter.) | Steps/s |")
    print("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
    for run in sys.argv[1:]:
        print(row(run))


if __name__ == "__main__":
    main()
