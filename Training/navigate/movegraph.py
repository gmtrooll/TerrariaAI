"""Physics movement graph of a world (docs/Physics Planner Design.md): every standing spot's moves, sampled with the
game's own Player.Update by the mod's `sample_moves` command, on several servers in parallel.

    python -m navigate.movegraph --world train_01 --loadout 1 --servers 6
    python -m navigate.movegraph --world train_01 --region 2080 250 64 64 --servers 2   # a small test area

Writes Training/movegraphs/<world>_l<loadout>.npy: int32 rows [from_x, from_y, to_x, to_y, ticks, hurt, program]
(to = -1 when the move never landed). Chunks already done are kept in a .partial file, so a stopped run resumes.
"""

import argparse
import json
import queue
import sys
import threading
import time
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from terraria_mcp.game import GameManager  # noqa: E402

OUT = Path(__file__).resolve().parents[1] / "movegraphs"
CHUNK = 64


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--world", required=True)
    p.add_argument("--loadout", type=int, default=1, help="0 no extra jump, 1 Tsunami in a Bottle")
    p.add_argument("--servers", type=int, default=6)
    p.add_argument("--region", type=int, nargs=4, metavar=("X", "Y", "W", "H"), default=None)
    p.add_argument("--first-port", type=int, default=7980)
    args = p.parse_args()

    OUT.mkdir(exist_ok=True)
    out = OUT / f"{args.world}_l{args.loadout}{'_region' if args.region else ''}.npy"
    partial = out.with_suffix(".partial.jsonl")
    done = {}
    if partial.exists():
        for line in partial.read_text().splitlines():
            d = json.loads(line)
            done[tuple(d["chunk"])] = d["edges"]

    games = GameManager()
    names = []
    try:
        for i in range(args.servers):
            name = f"graph_{args.world}_{i}"
            games.launch("server", world=args.world, name=name, bridge_port=args.first_port + i, extra_args=["-botlean"], wait=False)
            names.append(name)
        for name in names:
            games.wait_ready(name, timeout=600)
        size = games.call(names[0], "execute", code="return new[] { Main.maxTilesX, Main.maxTilesY };")["result"]
        if args.region:
            x0, y0, w, h = args.region
        else:  # whole world except the underworld (Navigate scenarios never go there)
            x0, y0, w, h = 50, 50, size[0] - 100, size[1] - 260
        chunks = [(x, y, min(CHUNK, x0 + w - x), min(CHUNK, y0 + h - y))
                  for x in range(x0, x0 + w, CHUNK) for y in range(y0, y0 + h, CHUNK)]
        todo = queue.Queue()
        for c in chunks:
            if c not in done:
                todo.put(c)
        print(f"{args.world} loadout {args.loadout}: {len(chunks)} chunks, {len(done)} already done, {args.servers} servers", flush=True)
        lock = threading.Lock()
        started = time.time()
        stats = {"nodes": 0, "ms": 0}

        def worker(name):
            while True:
                try:
                    c = todo.get_nowait()
                except queue.Empty:
                    return
                r = games.call(name, "sample_moves", timeout=1800, x=c[0], y=c[1], w=c[2], h=c[3], loadout=args.loadout)
                with lock:
                    done[c] = r["edges"]
                    stats["nodes"] += r["nodes"]
                    stats["ms"] += r["ms"]
                    with open(partial, "a") as f:
                        f.write(json.dumps({"chunk": list(c), "edges": r["edges"]}) + "\n")
                    n = len(done)
                    if n % 10 == 0 or n == len(chunks):
                        rate = (time.time() - started) / max(1, n - (len(chunks) - todo.qsize() - n))
                        print(f"  {n}/{len(chunks)} chunks, {stats['nodes']} nodes, {sum(len(e) for e in done.values())} edges, "
                              f"{time.time() - started:.0f} s", flush=True)

        threads = [threading.Thread(target=worker, args=(n,)) for n in names]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        edges = np.array([e for c in chunks for e in done.get(c, [])], np.int32).reshape(-1, 7)
        np.save(out, edges)
        edges.tofile(out.with_suffix(".bin"))  # raw int32 rows for the mod (-envgraph)
        partial.unlink(missing_ok=True)
        print(f"wrote {out}: {len(edges)} edges in {time.time() - started:.0f} s", flush=True)
    finally:
        for name in names:
            try:
                games.stop(name, timeout=10)
            except Exception:  # noqa: BLE001
                pass


if __name__ == "__main__":
    main()
