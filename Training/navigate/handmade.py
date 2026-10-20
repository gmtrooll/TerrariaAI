"""Hand-made test scenarios: small structures where the route has to start away from the target's direction, with
one thing varied at a time (where the target is, the ledge height, the pocket depth). Built in the `handmade` world
(normal gravity: rows 150-300 are below the space layer, Player.cs:23581), one structure per 80-tile region.

    python -m navigate.handmade --build                      # build the structures and save the world (once)
    python -m navigate.handmade --run navigate_v1 --repeats 8
Writes results/training/<run>/handmade.json and prints success per test and variant.

Tests (L = ledge's open end, F = floor row; the bot is 3 tiles tall, 1.25 wide):
  drop   bot on an upper ledge (1 tile thick, H tiles above the floor, open only on its left end, a wall closes the
         right), target on the floor at column L + c. The route always goes left past L, down, then back right.
  climb  the same structure; bot on the floor at column L + c, target on the ledge at L + 6. Route: left past L, up.
  pocket a C-shaped pocket opening toward the bot: back wall 4 tall, a lip D tiles long on top, 3 tiles of room
         inside. Bot on the floor to the right, target on the floor to the left: the bot has to jump over before it
         walks into the pocket.
"""

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path

WORLD = "handmade"
F = 262            # floor surface row (solid F..F+2); standing feet cells are on row F - 1
TOP = 150          # regions are cleared from this row down to the floor
REGION = 80        # tiles per structure
DISTANCES = Path(__file__).resolve().parents[1] / "scenarios" / "handmade_distances.json"  # planner route lengths
X0 = 200           # first region (48 regions end at 4040, inside the world's 4200 columns)


def tests():
    """(name, variant, structure, start, target, route estimate); structure = list of solid rects (x0, y0, x1, y1)."""
    out = []
    k = 0

    def region():
        nonlocal k
        x = X0 + k * REGION
        k += 1
        return x

    for H in (4, 6, 10):
        for c in (-6, -3, 0, 2, 4, 6, 8, 10, 14):
            r = region()
            L = r + 25
            U = F - H                                    # ledge row
            solids = [(L, U, L + 30, U), (L + 31, U - 6, L + 31, F - 1)]  # ledge, wall closing its right end
            start, target = (L + 6, U - 1), (L + c, F - 1)
            out.append(("drop", f"H{H} c{c:+d}", r, solids, start, target, 8 + H + abs(c + 2)))
    for H in (4, 6, 10):
        for c in (0, 3, 6, 10, 16):
            r = region()
            L = r + 25
            U = F - H
            solids = [(L, U, L + 30, U), (L + 31, U - 6, L + 31, F - 1)]
            start, target = (L + c, F - 1), (L + 6, U - 1)
            out.append(("climb", f"H{H} c{c:+d}", r, solids, start, target, c + 2 + H + 8))
    for D in (2, 3, 5):
        for gap in (2, 8):
            r = region()
            W = r + 30
            solids = [(W, F - 4, W, F - 1), (W, F - 4, W + D, F - 4)]  # back wall, lip (pocket opens to the right)
            start, target = (W + D + 1 + gap, F - 1), (W - 10, F - 1)
            out.append(("pocket", f"D{D} gap{gap}", r, solids, start, target, gap + D + 20))
    return out


def build_code(items) -> str:
    lines = ["void Solid(int x0, int y0, int x1, int y1) { for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) { var t = Main.tile[x, y]; t.ClearEverything(); t.ResetToType(TileID.GrayBrick); } }",
             "void Air(int x0, int y0, int x1, int y1) { for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) Main.tile[x, y].ClearEverything(); }"]
    for _, _, r, solids, _, _, _ in items:
        lines.append(f"Air({r}, {TOP}, {r + REGION - 1}, {F - 1});")
        lines.append(f"Solid({r}, {F}, {r + REGION - 1}, {F + 2});")
        lines.append(f"Solid({r}, {TOP}, {r}, {F - 1}); Solid({r + REGION - 1}, {TOP}, {r + REGION - 1}, {F - 1});")  # region walls
        for x0, y0, x1, y1 in solids:
            lines.append(f"Solid({x0}, {y0}, {x1}, {y1});")
    x_end = X0 + len(items) * REGION
    lines.append(f"WorldGen.RangeFrame({X0}, {TOP}, {x_end}, {F + 3});")
    lines.append("return \"built\";")
    return "\n".join(lines)


def build():
    sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
    from terraria_mcp.game import GameManager
    games = GameManager()
    games.launch("server", world=WORLD, name="handmade_build", bridge_port=7890)
    try:
        print(games.call("handmade_build", "execute", code=build_code(tests()), timeout=120))
        # Route lengths from the environment's own planner (Tsunami jump height), so time limits match training's.
        pairs = ", ".join(f"new[] {{ {s[0]}, {s[1]}, {t[0]}, {t[1]} }}" for _, _, _, _, s, t, _ in tests())
        code = ("var m = typeof(FirstMod_Sp.Common.Env.NavigateEnv).GetMethod(\"GetField\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);\n"
                f"var pairs = new[] {{ {pairs} }};\n"
                "return pairs.Select(q => ((PathDistanceField)m.Invoke(null, new object[] { q[2], q[3], 13 })).GetDistance(q[0], q[1])).ToArray();")
        d = games.call("handmade_build", "execute", code=code, timeout=120)["result"]
        DISTANCES.write_text(json.dumps({f"{n} {v}": int(x) for (n, v, *_), x in zip(tests(), d)}, indent=1))
        print(f"route lengths: {dict(zip([f'{t[0]} {t[1]}' for t in tests()], d))}")
    finally:
        games.stop("handmade_build", save=True)
    print(f"saved {WORLD} with {len(tests())} structures")


def evaluate(run: str, repeats: int, port: int, checkpoint: str, only: list = None, record: bool = False, obs_extra: str = "", tag: str = ""):
    from navigate.env import Curriculum, NavigateVecEnv
    from navigate.evaluate import RESULTS, load_policy

    run_dir = RESULTS / run
    config = json.loads((run_dir / "config.json").read_text())
    obs = config["obs"] + ("," + obs_extra if obs_extra else "")
    rows = []
    distances = json.loads(DISTANCES.read_text()) if DISTANCES.exists() else {}
    for name, variant, _, _, start, target, route in tests():
        route = distances.get(f"{name} {variant}", route)
        if route < 0:
            print(f"warning: {name} {variant} is unreachable for the planner")
            continue
        if only and f"{name} {variant}" not in only:
            continue
        for i in range(repeats):
            rows.append({"start_x": start[0], "start_y": start[1], "target_x": target[0], "target_y": target[1],
                         "path_distance": route, "band": name, "layer": variant, "id": f"{name}|{variant}|{i}", "loadout": 1})
    extra = ["-envrecord", str(run_dir / "handmade_failures")] if record else []
    env = NavigateVecEnv([WORLD], bots_per_server=min(32, len(rows)), port=port, obs=obs, curriculum=Curriculum(fixed=True),
                         fixed_scenarios={WORLD: rows}, frame_skip=config.get("frame_skip", 4), extra_args=extra)
    try:
        act = load_policy(run_dir, config, env, False, checkpoint)
        results = {}
        obs = env.reset()
        while len(results) < len(rows):
            env.step_async(act(obs))
            obs, _, dones, infos = env.step_wait()
            act.observe(dones)
            for info in infos:
                e = info.get("episode")
                if e and e["id"] not in results:
                    results[e["id"]] = e
    finally:
        env.close()
    table = defaultdict(list)
    for e in results.values():
        name, variant, _ = e["id"].split("|")
        table[(name, variant)].append(e["win"])
    out = {f"{n} {v}": round(sum(w) / len(w), 3) for (n, v), w in table.items()}
    if only:
        return
    (run_dir / f"handmade{'_' + tag if tag else ''}.json").write_text(
        json.dumps({"run": run, "checkpoint": checkpoint, "obs": obs, "repeats": repeats, "success": out}, indent=2))
    current = None
    for (n, v), w in table.items():
        if n != current:
            print(n)
            current = n
        print(f"   {v:12s} {sum(w)}/{len(w)}")


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--build", action="store_true")
    p.add_argument("--run")
    p.add_argument("--repeats", type=int, default=8)
    p.add_argument("--port", type=int, default=5786)
    p.add_argument("--checkpoint", default="model.pt")
    p.add_argument("--only", nargs="+", help='variants to run, e.g. "drop H6 c+6"')
    p.add_argument("--obs-extra", default="", help='observation settings added to the run\'s, e.g. "los=1"')
    p.add_argument("--tag", default="", help="written to handmade_<tag>.json")
    p.add_argument("--record", action="store_true", help="write failed episodes to <run>/handmade_failures_handmade.jsonl")
    args = p.parse_args()
    if args.build:
        build()
    if args.run:
        evaluate(args.run, args.repeats, args.port, args.checkpoint, args.only, args.record, args.obs_extra, args.tag)


if __name__ == "__main__":
    main()
