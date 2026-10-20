"""How high a player gets jumping from the floor of a liquid pool, by liquid, pool depth and loadout. The grid planner
(PathDistanceField) gives a jump the same height in any cell, but in honey Player.HoneyCollision moves the body at a
quarter of its velocity (water: half, WaterCollision) and gravity changes, so a deep pool may be impossible to leave.

Pools are built in the `handmade` world under its test structures (the world is not saved): 4 tiles wide, walls 50
tall, liquid D tiles deep. One bot per (liquid, depth, loadout, jump pattern), standing on the pool floor:
  hold    jump held the whole time (one jump);
  double  jump held until the apex (velocity.Y >= 0), released for a tick, then held again (Tsunami's extra jump);
  mash    jump pressed 4 ticks, released 4, repeated (would show swimming or repeated jumps);
  at<T>   (--sweep) jump held, released at tick T for one tick, then held: the best moment for the extra jump.

    python -m navigate.liquid_jump            # liquids, depths, loadouts and the three patterns
    python -m navigate.liquid_jump --sweep    # honey with Tsunami: the extra jump's timing per depth
    python -m navigate.liquid_jump --floor    # honey block floors: one jump, mashing, the extra jump's timing
Prints the highest rise of the feet above the pool floor in tiles; writes results/training/liquid_jump.json.
"""

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from terraria_mcp.game import GameManager  # noqa: E402

OUT = Path(__file__).resolve().parents[2] / "results" / "training"
FLOOR = 340       # pool floor surface row (solid FLOOR..FLOOR+2); feet stand on row FLOOR - 1
TOP = 285
X0 = 300
WIDTH = 6         # per pool: wall, 4 inner columns, shared wall
DEPTHS = [0, 1, 2, 3, 4, 5, 6, 8, 10, 14]
LIQUIDS = {"water": 0, "honey": 2}  # LiquidID
PATTERNS = ["hold", "double", "mash"]
TICKS = 240


def cases(sweep=False, floor=False):
    if floor:  # honey block floor (Player.sticky: jumpHeight / 10, jumpSpeed / 5 while standing on it)
        out = [{"liquid": "honey_block", "depth": 0, "tsunami": t, "pattern": p} for t in (False, True) for p in ("hold", "mash")]
        return out + [{"liquid": "honey_block", "depth": 0, "tsunami": True, "pattern": f"at{t}"} for t in range(1, 41)]
    if sweep:
        return [{"liquid": "honey", "depth": d, "tsunami": True, "pattern": f"at{t}"} for d in (1, 2, 3, 4, 5, 6, 8) for t in range(4, 100, 4)]
    out = []
    for liquid in LIQUIDS:
        for depth in DEPTHS:
            if depth == 0 and liquid != "water":
                continue
            for tsunami in (False, True):
                for pattern in PATTERNS:
                    if not tsunami and pattern == "double":
                        continue
                    out.append({"liquid": "dry" if depth == 0 else liquid, "depth": depth, "tsunami": tsunami, "pattern": pattern})
    return out


def build_code(items):
    lines = ["void Solid(int x0, int y0, int x1, int y1) { for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) { var t = Main.tile[x, y]; t.ClearEverything(); t.ResetToType(TileID.GrayBrick); } }",
             "void Air(int x0, int y0, int x1, int y1) { for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) Main.tile[x, y].ClearEverything(); }",
             "void Liquid(int x0, int y0, int x1, int y1, int type) { for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) { var t = Main.tile[x, y]; t.LiquidType = type; t.LiquidAmount = 255; } }"]
    x_end = X0 + len(items) * WIDTH
    lines.append(f"Air({X0}, {TOP}, {x_end}, {FLOOR - 1}); Solid({X0}, {FLOOR}, {x_end}, {FLOOR + 2});")
    for i, c in enumerate(items):
        x = X0 + i * WIDTH
        lines.append(f"Solid({x}, {TOP}, {x}, {FLOOR - 1});")
        if c["liquid"] == "honey_block":
            lines.append(f"for (int x = {x + 1}; x < {x + WIDTH}; x++) Main.tile[x, {FLOOR}].ResetToType(TileID.HoneyBlock);")
        if c["depth"]:
            lines.append(f"Liquid({x + 1}, {FLOOR - c['depth']}, {x + WIDTH - 1}, {FLOOR - 1}, {LIQUIDS[c['liquid']]});")
    lines.append(f"Solid({x_end}, {TOP}, {x_end}, {FLOOR - 1});")
    lines.append(f"WorldGen.RangeFrame({X0}, {TOP}, {x_end}, {FLOOR + 3});")
    lines.append("return \"built\";")
    return "\n".join(lines)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--port", type=int, default=7895)
    p.add_argument("--sweep", action="store_true")
    p.add_argument("--floor", action="store_true", help="honey block floors instead of liquids")
    args = p.parse_args()
    items = cases(args.sweep, args.floor)
    games = GameManager()
    name = "liquid_jump"
    games.launch("server", world="handmade", name=name, bridge_port=args.port)
    try:
        games.call(name, "pause")
        print(games.call(name, "execute", code=build_code(items), timeout=120)["result"])
        games.call(name, "spawn_bots", count=len(items), x=X0 + 3, y=FLOOR - 2, spread=0)
        bots = games.call(name, "state", all=True)["bots"]
        assert len(bots) >= len(items), f"{len(bots)} bots for {len(items)} cases"
        for i, c in enumerate(items):
            games.call(name, "execute", code=f"var p = Main.player[{bots[i]['player']}]; p.armor[3].SetDefaults({'ItemID.TsunamiInABottle' if c['tsunami'] else 'ItemID.None'}); return 0;")
            games.call(name, "teleport", bot=i, x=X0 + i * WIDTH + 2.5, y=FLOOR - 2.5)
        games.call(name, "step", ticks=30, timeout=60)  # land and settle (the liquid too)
        for i in range(len(items)):
            games.call(name, "teleport", bot=i, x=X0 + i * WIDTH + 2.5, y=FLOOR - 2.5)
        games.call(name, "step", ticks=20, timeout=60)
        start = [s["y"] for s in games.call(name, "state", all=True)["bots"][:len(items)]]
        best = list(start)
        released = [False] * len(items)
        apex = [False] * len(items)
        for t in range(TICKS):
            state = games.call(name, "state", all=True)["bots"][:len(items)]
            for i, (c, s) in enumerate(zip(items, state)):
                best[i] = min(best[i], s["y"])
                if c["pattern"] == "hold":
                    jump = True
                elif c["pattern"] == "mash":
                    jump = t % 8 < 4
                elif c["pattern"].startswith("at"):
                    jump = t != int(c["pattern"][2:])
                else:
                    if not apex[i] and t > 3 and s["vy"] >= 0:
                        apex[i] = True
                        released[i] = True
                        jump = False
                    else:
                        jump = True
                        released[i] = False
                games.call(name, "input", bot=i, jump=jump, ticks=1, wait=False)
            games.call(name, "step", ticks=1, timeout=30)
        for c, s0, b in zip(items, start, best):
            c["rise_tiles"] = round(s0 - b, 2)
        for c in items:
            print(f"{c['liquid']:6s} depth {c['depth']:2d} {'tsunami' if c['tsunami'] else 'none   '} {c['pattern']:7s} rise {c['rise_tiles']:6.2f}")
        (OUT / f"liquid_jump{'_sweep' if args.sweep else '_floor' if args.floor else ''}.json").write_text(json.dumps(items, indent=1))
    finally:
        games.stop(name)


if __name__ == "__main__":
    main()
