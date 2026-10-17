"""Benchmark runner: launches configurations, runs the mod's `bench` command in every instance at once, and appends
one row per configuration to results/bench/bench.csv.

    python -m terraria_mcp.bench run --mode server --bots 1 4 16 --processes 1 --repeats 3 --label M1
    python -m terraria_mcp.bench physics --modes server client --bots 4
"""

import argparse
import csv
import json
import math
import statistics
import threading
import time
from datetime import datetime
from pathlib import Path

from . import config
from .game import GameManager

RESULTS_DIR = config.MOD_SOURCE / "results" / "bench"
CSV_PATH = RESULTS_DIR / "bench.csv"
SECTIONS = ["players", "npcs", "gores", "projectiles", "items", "dusts", "time", "world", "observe", "outside"]
COLUMNS = (["time", "label", "mode", "world", "processes", "bots_per_process", "frame_skip", "observe", "ticks",
            "repeat", "ticks_per_s", "total_decisions_per_s", "ms_per_tick", "us_per_bot_tick"]
           + [f"ms_{s}" for s in SECTIONS]
           + ["cpu_cores", "working_set_mb", "private_mb", "managed_mb", "alloc_kb_per_tick", "gc0", "gc1", "gc2",
              "errors", "npcs_active", "notes"])


def bench_all(games: GameManager, names: list, timeout: float, **bench_args) -> list:
    """Start `bench` in every instance at the same moment and collect the replies."""
    results = [None] * len(names)
    errors = [None] * len(names)
    barrier = threading.Barrier(len(names))

    def worker(i, name):
        try:
            barrier.wait()
            results[i] = games.call(name, "bench", timeout=timeout, **bench_args)
        except Exception as e:  # noqa: BLE001 - reported per instance
            errors[i] = e

    threads = [threading.Thread(target=worker, args=(i, n)) for i, n in enumerate(names)]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    for name, error in zip(names, errors):
        if error is not None:
            raise RuntimeError(f"bench failed in {name}: {error}")
    return results


def launch_many(games: GameManager, mode: str, world: str, processes: int, bots: int, timeout: float = 300,
                extra_args: list = None) -> list:
    """Launch instances in parallel and wait until all are in their world."""
    names = [f"bench_{mode}_{i}" for i in range(processes)]
    errors = []

    def worker(name):
        try:
            games.launch(mode, world=world, bots=bots, name=name, timeout=timeout, extra_args=extra_args)
        except Exception as e:  # noqa: BLE001
            errors.append(f"{name}: {e}")

    threads = [threading.Thread(target=worker, args=(n,)) for n in names]
    for t in threads:
        t.start()
        time.sleep(1.0)  # stagger: all instances read the same files at start-up
    for t in threads:
        t.join()
    if errors:
        raise RuntimeError("launch failed: " + " | ".join(errors))
    return names


def summarize(label, mode, world, processes, bots, frame_skip, observe, ticks, repeat, results, notes="") -> dict:
    """One CSV row for a configuration: throughput summed over processes, costs averaged."""
    def mean(key):
        return statistics.fmean(r[key] for r in results)

    row = {
        "time": datetime.now().isoformat(timespec="seconds"), "label": label, "mode": mode, "world": world,
        "processes": processes, "bots_per_process": bots, "frame_skip": frame_skip, "observe": observe,
        "ticks": ticks, "repeat": repeat,
        "ticks_per_s": round(mean("ticks_per_s"), 1),
        "total_decisions_per_s": round(sum(r["decisions_per_s"] for r in results), 1),
        "ms_per_tick": round(mean("ms_per_tick"), 4),
        "us_per_bot_tick": round(mean("us_per_bot_tick"), 2),
        "cpu_cores": round(sum(r["cpu_cores"] for r in results), 2),
        "working_set_mb": round(mean("working_set_mb")),
        "private_mb": round(mean("private_mb")),
        "managed_mb": round(mean("managed_mb")),
        "alloc_kb_per_tick": round(mean("alloc_kb_per_tick"), 2),
        "gc0": sum(r["gc"]["gen0"] for r in results),
        "gc1": sum(r["gc"]["gen1"] for r in results),
        "gc2": sum(r["gc"]["gen2"] for r in results),
        "errors": sum(r["errors"] for r in results),
        "npcs_active": round(statistics.fmean(r["active"]["npcs"] for r in results), 1),
        "notes": notes,
    }
    for s in SECTIONS:
        row[f"ms_{s}"] = round(statistics.fmean(r["sections_ms"][s] for r in results), 4)
    return row


def append_rows(rows: list) -> None:
    RESULTS_DIR.mkdir(parents=True, exist_ok=True)
    new_file = not CSV_PATH.exists()
    with open(CSV_PATH, "a", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=COLUMNS)
        if new_file:
            writer.writeheader()
        writer.writerows(rows)


def run_matrix(mode="server", world=config.DEFAULT_WORLD, bots_list=(4,), processes_list=(1,), frame_skip=4,
               ticks=3000, warmup=3000, observe=False, repeats=1, label="", notes="", extra_args=None,
               games=None) -> list:
    """For each (processes, bots): launch, bench `repeats` times, stop. Returns the CSV rows (also appended)."""
    games = games or GameManager()
    rows = []
    for processes in processes_list:
        for bots in bots_list:
            names = launch_many(games, mode, world, processes, bots, extra_args=extra_args)
            try:
                timeout = max(120.0, ticks / 50)
                for repeat in range(repeats):
                    results = bench_all(games, names, timeout=timeout, ticks=ticks, warmup=warmup,
                                        frame_skip=frame_skip, observe=observe, seed=repeat + 1)
                    row = summarize(label, mode, world, processes, bots, frame_skip, observe, ticks, repeat,
                                    results, notes)
                    rows.append(row)
                    append_rows([row])
                    print(f"{label} {mode} p={processes} bots={bots} rep={repeat}: {row['ticks_per_s']} ticks/s, "
                          f"{row['total_decisions_per_s']} decisions/s, {row['ms_per_tick']} ms/tick, "
                          f"{row['us_per_bot_tick']} us/bot-tick, cpu {row['cpu_cores']}, "
                          f"ws {row['working_set_mb']} MB, errors {row['errors']}", flush=True)
            finally:
                for name in names:
                    games.stop(name)
    return rows


def physics_check(modes=("server", "client"), world=config.DEFAULT_WORLD, bots=4, ticks=1200, frame_skip=4,
                  seed=7, variants=((),), games=None) -> dict:
    """Same start positions and the same action sequence in each mode and variant (a list of extra launch
    arguments, e.g. ("-botlean",)); report how far trajectories diverge. Each combination runs twice to separate
    real differences from run-to-run noise."""
    games = games or GameManager()
    runs = {}
    for mode in modes:
        for variant in variants:
            for attempt in range(2):
                name = f"physics_{mode}"
                games.launch(mode, world=world, bots=bots, name=name, extra_args=list(variant))
                try:
                    result = games.call(name, "bench", timeout=120, ticks=ticks, warmup=0, warmup_seconds=0,
                                        frame_skip=frame_skip, seed=seed, record=True, place_bots=True, isolate=True)
                finally:
                    games.stop(name)
                runs[" ".join([mode, *variant]) + f"#{attempt}"] = result["trajectories"]

    def max_deviation(a, b):
        worst = 0.0
        for ta, tb in zip(a, b):
            for i in range(0, min(len(ta), len(tb)), 2):
                worst = max(worst, math.hypot(ta[i] - tb[i], ta[i + 1] - tb[i + 1]))
        return round(worst, 4)

    keys = list(runs)
    report = {"ticks": ticks, "bots": bots, "seed": seed, "max_deviation_tiles": {}}
    for i, a in enumerate(keys):
        for b in keys[i + 1:]:
            report["max_deviation_tiles"][f"{a} vs {b}"] = max_deviation(runs[a], runs[b])
    RESULTS_DIR.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    (RESULTS_DIR / f"physics_{stamp}.json").write_text(json.dumps({"report": report, "trajectories": runs}))
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)
    run = sub.add_parser("run")
    run.add_argument("--mode", default="server", choices=["server", "client"])
    run.add_argument("--world", default=config.DEFAULT_WORLD)
    run.add_argument("--bots", type=int, nargs="+", default=[4])
    run.add_argument("--processes", type=int, nargs="+", default=[1])
    run.add_argument("--frame-skip", type=int, default=4)
    run.add_argument("--ticks", type=int, default=3000)
    run.add_argument("--warmup", type=int, default=3000)
    run.add_argument("--observe", action="store_true")
    run.add_argument("--repeats", type=int, default=1)
    run.add_argument("--label", default="")
    run.add_argument("--notes", default="")
    run.add_argument("--extra-arg", action="append", default=[],
                     help="game launch argument, repeatable; use the = form for dashes: --extra-arg=-botlean")
    phys = sub.add_parser("physics")
    phys.add_argument("--modes", nargs="+", default=["server", "client"])
    phys.add_argument("--bots", type=int, default=4)
    phys.add_argument("--ticks", type=int, default=1200)
    phys.add_argument("--variant", action="append", default=None,
                      help='extra launch args of one variant, repeatable; "" = none, --variant=-botlean')
    args = parser.parse_args()

    games = GameManager()
    try:
        if args.command == "run":
            run_matrix(args.mode, args.world, args.bots, args.processes, args.frame_skip, args.ticks, args.warmup,
                       args.observe, args.repeats, args.label, args.notes, extra_args=args.extra_arg, games=games)
        else:
            variants = [tuple(v.split()) for v in (args.variant or [""])]
            print(json.dumps(physics_check(args.modes, bots=args.bots, ticks=args.ticks, variants=variants,
                                           games=games), indent=2))
    finally:
        games.stop_all()


if __name__ == "__main__":
    main()
