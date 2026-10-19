"""Add physics route times to a world's scenarios (docs/Physics Planner Design.md): for every scenario, the cheapest
time in ticks from its start to its target over the world's movement graph (Training/movegraphs/<world>_l<loadout>.npy),
or -1 when the start cannot physically reach the target. Writes the field "physics_ticks" into the scenario file.

    python -m navigate.annotate_physics --world train_01 --loadout 1
"""

import argparse
import collections
import json

import numpy as np
from scipy.sparse import csr_matrix
from scipy.sparse.csgraph import dijkstra

from navigate.env import LOADOUTS
from navigate.movegraph import OUT as GRAPHS
from terraria_mcp import scenarios as scenario_files

GOAL_RADIUS = 1.5


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--world", required=True)
    p.add_argument("--loadout", type=int, default=1)
    args = p.parse_args()

    edges = np.load(GRAPHS / f"{args.world}_l{args.loadout}.npy")
    ok = (edges[:, 2] >= 0) & (edges[:, 5] == 0) & (edges[:, 4] > 0)
    nodes = {}
    for x, y in np.concatenate([edges[:, 0:2], edges[ok][:, 2:4]]):
        nodes.setdefault((int(x), int(y)), len(nodes))
    src = np.array([nodes[(int(x), int(y))] for x, y in edges[ok][:, 0:2]])
    dst = np.array([nodes[(int(x), int(y))] for x, y in edges[ok][:, 2:4]])
    n = len(nodes)
    # Reversed graph (target -> start direction), keeping the cheapest of parallel moves.
    best = collections.defaultdict(lambda: 1 << 30)
    for a, b, t in zip(src, dst, edges[ok][:, 4]):
        if t < best[(b, a)]:
            best[(b, a)] = int(t)
    keys = np.array(list(best.keys())) if best else np.zeros((0, 2), int)
    rev = csr_matrix((np.array(list(best.values()), float), (keys[:, 0], keys[:, 1])), shape=(n, n))

    suffix = [s for s, code in LOADOUTS.values() if code == args.loadout][0]
    rows = scenario_files.load(args.world, suffix)
    by_target = collections.defaultdict(list)
    for r in rows:
        by_target[(r["target_x"], r["target_y"])].append(r)
    reachable = 0
    for (tx, ty), group in by_target.items():
        # Goal nodes: body centre (about 1.3 tiles above the feet) within the goal radius of the target point.
        cx, cy = tx + 0.5, ty - 0.3
        goals = [i for (x, y), i in nodes.items() if abs(x - tx) <= 4 and abs(y - ty) <= 5
                 and (x + 0.5 - cx) ** 2 + (y - 0.3 - cy) ** 2 <= GOAL_RADIUS ** 2]
        if goals:
            dist = dijkstra(rev, indices=goals, min_only=True)
        for r in group:
            i = nodes.get((r["start_x"], r["start_y"]))
            t = dist[i] if goals and i is not None else np.inf
            r["physics_ticks"] = int(t) if np.isfinite(t) else -1
            reachable += r["physics_ticks"] >= 0
    path = scenario_files.SCENARIO_DIR / f"{args.world}{suffix}.jsonl"
    path.write_text("".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")
    print(f"{args.world} loadout {args.loadout}: {len(nodes)} nodes, {int(ok.sum())} moves; "
          f"{reachable}/{len(rows)} scenarios physically reachable")


if __name__ == "__main__":
    main()
