"""Render failed episodes recorded by `navigate.evaluate --record`: the planned route (magenta), the bot's path
(cyan), start (green) and target (red), on the real tiles. For checking whether a failure was the policy's or the
planner's (an impossible route).

    python -m navigate.failures --run stride2 --count 40
Writes results/training/<run>/failures/<world>_<n>.png and index.json (scenario, result, picture).
"""

import argparse
import json
import random

from navigate.evaluate import RESULTS
from terraria_mcp import tilemap
from terraria_mcp.game import GameManager


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--run", required=True)
    p.add_argument("--count", type=int, default=40, help="failures to render (sampled evenly over worlds)")
    p.add_argument("--seed", type=int, default=0)
    args = p.parse_args()

    run_dir = RESULTS / args.run
    out = run_dir / "failures"
    out.mkdir(exist_ok=True)
    by_world = {}
    for f in sorted(run_dir.glob("failures_*.jsonl")):
        seen = set()
        for line in f.read_text(encoding="utf-8").splitlines():
            e = json.loads(line)
            key = (tuple(e["start"]), tuple(e["target"]))
            if key not in seen:  # bots keep cycling the fixed list: keep each scenario once
                seen.add(key)
                by_world.setdefault(e["world"], []).append(e)
    rng = random.Random(args.seed)
    index = []
    games = GameManager()
    for world, rows in by_world.items():
        rng.shuffle(rows)
        name = f"failures_{world}"
        games.launch("server", world=world, name=name)
        try:
            for n, e in enumerate(rows[:args.count // len(by_world)]):
                pts = e["route"] + e["trajectory"] + [e["target"]]
                xs, ys = [q[0] for q in pts], [q[1] for q in pts]
                x0, y0 = min(xs) - 10, min(ys) - 10
                w, h = min(max(xs) - x0 + 11, 400), min(max(ys) - y0 + 11, 400)
                tiles = games.call(name, "tiles", x=x0, y=y0, w=w, h=h)
                png = tilemap.render(tiles, scale=6, paths=[e["route"], e["trajectory"]],
                                     markers=[(e["start"][0], e["start"][1], (0, 255, 0)), (e["target"][0], e["target"][1], (255, 0, 0))])
                pic = out / f"{world}_{n:02d}.png"
                pic.write_bytes(png)
                index.append({"picture": pic.name, "world": world, "start": e["start"], "target": e["target"],
                              "result": e["result"], "route_length": e["start_distance"], "decisions": e["steps"]})
        finally:
            games.stop(name)
    (out / "index.json").write_text(json.dumps(index, indent=1))
    print(f"{len(index)} pictures in {out}")


if __name__ == "__main__":
    main()
