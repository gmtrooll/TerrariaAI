"""Scenario sets per movement category (Common/Scenarios/CategoryScenarios.cs) for existing worlds: test sets for the
held-out worlds, training sets for the training worlds. One file per world: Training/scenarios/<world>_cat.jsonl, rows
tagged with their category and band (by route distance).

    python -m navigate.category_sets --worlds eval_01 eval_02 --per-category 100
Evaluate with `navigate.evaluate --set _cat`; `navigate.category_sets --report RUN TAG` prints success per category.
"""

import argparse
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from terraria_mcp.game import GameManager  # noqa: E402
from terraria_mcp.scenarios import SCENARIO_DIR, load  # noqa: E402

CATEGORIES = ["water", "honey", "ice", "honey_block", "web", "platform", "door", "switchback"]
RESULTS = Path(__file__).resolve().parents[2] / "results" / "training"


def band_of(d):
    return "5-15" if d < 15 else "15-30" if d < 30 else "30-60" if d < 60 else "60-120"


def generate(worlds, per_category, seed, suffix):
    games = GameManager()
    for wi, world in enumerate(worlds):
        name = f"catset_{world}"
        games.launch("server", world=world, name=name, bridge_port=7891)
        rows = []
        try:
            for ci, c in enumerate(CATEGORIES):
                lo, hi = (20, 80) if c == "switchback" else (8, 40)
                got = []
                for attempt in range(8):  # rare categories need several calls
                    r = games.call(name, "generate_category_scenarios", timeout=1200, category=c, count=per_category - len(got),
                                   min_distance=lo, max_distance=hi, seed=seed + wi * 1000 + ci * 100 + attempt, max_jump=13, max_air_gap=5)
                    got += r["rows"]
                    if len(got) >= per_category or r["count"] == 0 and attempt >= 2:
                        break
                for s in got[:per_category]:
                    s.update(world=world, band=band_of(s["path_distance"]), max_jump=13, max_air_gap=5)
                    rows.append(s)
                print(f"{world} {c:12s} {min(len(got), per_category):4d} (anchors {r['anchors']})", flush=True)
        finally:
            games.stop(name)
        path = SCENARIO_DIR / f"{world}{suffix}.jsonl"
        path.write_text("".join(json.dumps(s) + "\n" for s in rows), encoding="utf-8")
        print(f"wrote {path}: {len(rows)} scenarios", flush=True)


def report(runs_tags, worlds, suffix):
    cats = {}
    for w in worlds:
        for i, s in enumerate(load(w, suffix)):
            cats[f"{w}{suffix}:{i}"] = s["category"]
    header = f"{'category':12s} {'n':>5s} " + " ".join(f"{rt:>24s}" for rt in runs_tags)
    print(header)
    table = defaultdict(dict)
    n = Counter(cats.values())
    for rt in runs_tags:
        run, tag = rt.split(":")
        eps = json.loads((RESULTS / run / f"eval_{tag}.json").read_text())["episodes"]
        by = defaultdict(list)
        for e in eps:
            by[cats[e["id"]]].append(e["win"])
        for c, w in by.items():
            table[c][rt] = 100 * sum(w) / len(w)
    for c in CATEGORIES:
        if c in table:
            print(f"{c:12s} {n[c]:5d} " + " ".join(f"{table[c].get(rt, float('nan')):23.1f}%" for rt in runs_tags))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--worlds", nargs="+", default=["eval_01", "eval_02"])
    p.add_argument("--per-category", type=int, default=100)
    p.add_argument("--seed", type=int, default=31000)
    p.add_argument("--suffix", default="_cat")
    p.add_argument("--report", nargs="+", metavar="RUN:TAG", help="success per category of evaluations, e.g. navigate_v3:cat")
    args = p.parse_args()
    if args.report:
        report(args.report, args.worlds, args.suffix)
    else:
        generate(args.worlds, args.per_category, args.seed, args.suffix)


if __name__ == "__main__":
    main()
