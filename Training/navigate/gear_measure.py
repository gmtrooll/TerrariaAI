"""Measure what each loadout lets a player do, in the game, and write the loadout table (Training/loadouts.json) that the
mod reads with -envloadouts (Common/Env/Loadouts.cs). Gear is described to the policy by these measured abilities, not
by item ids, so an item never seen in training is a new combination of known numbers:

  jump_apex    feet rise of one held jump from a flat floor (tiles);
  total_apex   feet rise when every extra jump is started at the top of the previous one (tiles);
  extra_jumps  number of extra jumps the gear gives (enabled ExtraJump states);
  run_speed    top horizontal speed: the game's max(maxRunSpeed, accRunSpeed) with the gear on (px per tick); the speed
               measured holding right on a long floor is kept as measured_run_speed.

The planner's jump height for the loadout is total_apex rounded down. Built in the `handmade` world below its test
structures (the world is not saved): one dry shaft per jump bot, one long corridor per run bot.

    python -m navigate.gear_measure
"""

import argparse
import json
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from terraria_mcp.game import GameManager  # noqa: E402

TABLE = Path(__file__).resolve().parents[1] / "loadouts.json"
OUT = Path(__file__).resolve().parents[2] / "results" / "training"

# code, name, items (ItemID names), split. Codes 0 and 1 keep their meaning from the two-loadout runs.
LOADOUTS = [
    (0, "none", [], "train"),
    (1, "tsunami", ["TsunamiInABottle"], "train"),
    (2, "cloud", ["CloudinaBottle"], "train"),
    (3, "sandstorm", ["SandstorminaBottle"], "train"),
    (4, "fart", ["FartinaJar"], "train"),
    (5, "balloon", ["ShinyRedBalloon"], "train"),
    (6, "cloud_balloon", ["CloudinaBalloon"], "train"),
    (7, "hermes", ["HermesBoots"], "train"),
    (8, "tsunami_hermes", ["TsunamiInABottle", "HermesBoots"], "train"),
    (9, "balloon_hermes", ["ShinyRedBalloon", "HermesBoots"], "train"),
    # several extra jumps at once (Oct 21: Bundle of Balloons, 3 extra jumps and a 60-tile apex, was far outside the
    # trained range and the gear-aware policy collapsed on it); Blizzard stays held out
    (10, "cloud_fart", ["CloudinaBottle", "FartinaJar"], "train"),
    (11, "sandstorm_cloud", ["SandstorminaBottle", "CloudinaBottle"], "train"),
    (12, "tsunami_fart_cloud", ["TsunamiInABottle", "FartinaJar", "CloudinaBottle"], "train"),
    (13, "balloon_tsunami_sandstorm_cloud", ["ShinyRedBalloon", "TsunamiInABottle", "SandstorminaBottle", "CloudinaBottle"], "train"),
    # held out: never worn in training
    (20, "blizzard", ["BlizzardinaBottle"], "test"),
    (21, "frogleg", ["FrogLeg"], "test"),
    (22, "bundle", ["BundleofBalloons"], "test"),
    (23, "sandstorm_balloon_hermes", ["SandstorminaBalloon", "HermesBoots"], "test"),
    (24, "blizzard_balloon", ["BlizzardinaBalloon"], "test"),
]

FLOOR, TOP, X0, WIDTH = 340, 270, 300, 6   # jump shafts: floor row, open up to TOP
LANE_Y0, LANE_GAP, LANE_X0, LANE_X1 = 420, 6, 300, 900
TICKS = 300


def build_code(n_jump, n_run):
    lines = ["void Solid(int x0, int y0, int x1, int y1) { for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) { var t = Main.tile[x, y]; t.ClearEverything(); t.ResetToType(TileID.GrayBrick); } }",
             "void Air(int x0, int y0, int x1, int y1) { for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) Main.tile[x, y].ClearEverything(); }"]
    x_end = X0 + n_jump * WIDTH
    lines.append(f"Air({X0}, {TOP}, {x_end}, {FLOOR - 1}); Solid({X0}, {FLOOR}, {x_end}, {FLOOR + 2});")
    for i in range(n_jump + 1):
        lines.append(f"Solid({X0 + i * WIDTH}, {TOP}, {X0 + i * WIDTH}, {FLOOR - 1});")
    for i in range(n_run):
        y = LANE_Y0 + i * LANE_GAP
        lines.append(f"Solid({LANE_X0}, {y - 5}, {LANE_X1}, {y - 5}); Air({LANE_X0}, {y - 4}, {LANE_X1}, {y - 1}); Solid({LANE_X0}, {y}, {LANE_X1}, {y + 1});")
        lines.append(f"Solid({LANE_X0}, {y - 4}, {LANE_X0}, {y - 1}); Solid({LANE_X1}, {y - 4}, {LANE_X1}, {y - 1});")
    lines.append(f"WorldGen.RangeFrame({X0}, {TOP}, {max(x_end, LANE_X1)}, {LANE_Y0 + n_run * LANE_GAP + 2});")
    lines.append("return \"built\";")
    return "\n".join(lines)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--port", type=int, default=7895)
    args = p.parse_args()
    n = len(LOADOUTS)
    # bots: [hold jump x n][chain jumps x n][run x n]
    games = GameManager()
    name = "gear_measure"
    games.launch("server", world="handmade", name=name, bridge_port=args.port)
    try:
        games.call(name, "pause")
        print(games.call(name, "execute", code=build_code(2 * n, n), timeout=120)["result"])
        games.call(name, "spawn_bots", count=3 * n, x=X0 + 3, y=FLOOR - 2, spread=0)
        bots = games.call(name, "state", all=True)["bots"][:3 * n]
        assert len(bots) == 3 * n, f"{len(bots)} bots for {3 * n}"
        for k in range(3 * n):
            items = LOADOUTS[k % n][2]
            ids = ", ".join(f"\"{it}\"" for it in items)
            code = (f"var p = Main.player[{bots[k]['player']}]; var names = new string[] {{ {ids} }};\n"
                    "for (int i = 0; i < 5; i++) { int id = 0; if (i < names.Length && !ItemID.Search.TryGetId(names[i], out id)) throw new System.Exception(\"unknown item \" + names[i]); p.armor[3 + i].SetDefaults(id); }\n"
                    "return 0;")
            games.call(name, "execute", code=code)

        def place():
            for k in range(3 * n):
                if k < 2 * n:
                    games.call(name, "teleport", bot=k, x=X0 + k * WIDTH + 2.5, y=FLOOR - 2.5)
                else:
                    games.call(name, "teleport", bot=k, x=LANE_X0 + 4, y=LANE_Y0 + (k - 2 * n) * LANE_GAP - 2.5)
        place()
        games.call(name, "step", ticks=30, timeout=60)
        place()
        games.call(name, "step", ticks=20, timeout=60)
        extra = games.call(name, "execute", code=(
            "var ids = new int[] { " + ", ".join(str(b["player"]) for b in bots[:n]) + " };\n"
            "return ids.Select(i => Main.player[i].ExtraJumps.ToArray().Count(s => s.Enabled)).ToArray();"))["result"]
        stat_speed = games.call(name, "execute", code=(
            "var ids = new int[] { " + ", ".join(str(b["player"]) for b in bots[:n]) + " };\n"
            "return ids.Select(i => System.Math.Max(Main.player[i].maxRunSpeed, Main.player[i].accRunSpeed)).ToArray();"))["result"]
        state = games.call(name, "state", all=True)["bots"][:3 * n]
        start_y = [s["y"] for s in state]
        best_rise = [0.0] * (2 * n)
        top_speed = [0.0] * n
        last_press = [0] * n
        for t in range(TICKS):
            state = games.call(name, "state", all=True)["bots"][:3 * n]
            for k, s in enumerate(state):
                if k < 2 * n:
                    best_rise[k] = max(best_rise[k], start_y[k] - s["y"])
                if k < n:
                    games.call(name, "input", bot=k, jump=True, ticks=1, wait=False)
                elif k < 2 * n:
                    i = k - n
                    jump = True
                    if t > 3 and s["vy"] >= 0 and t - last_press[i] > 4:
                        jump = False          # release for one tick at the apex, press again next tick
                        last_press[i] = t + 1
                    games.call(name, "input", bot=k, jump=jump, ticks=1, wait=False)
                else:
                    if t >= TICKS - 100:  # steady state: the mean speed over the last 100 ticks
                        top_speed[k - 2 * n] += abs(s["vx"]) / 100
                    games.call(name, "input", bot=k, right=True, ticks=1, wait=False)
            games.call(name, "step", ticks=1, timeout=30)
    finally:
        games.stop(name)
    table = []
    for i, (code, lname, items, split) in enumerate(LOADOUTS):
        single, total = round(best_rise[i], 2), round(max(best_rise[i], best_rise[n + i]), 2)
        row = {"code": code, "name": lname, "items": items, "split": split, "jump_tiles": max(6, int(math.floor(total))),
               "features": [single, total, int(extra[i]), round(float(stat_speed[i]), 2)], "measured_run_speed": round(top_speed[i], 2)}
        table.append(row)
        print(f"{lname:26s} {split:5s} jump {single:6.2f}  total {total:6.2f}  extra {extra[i]}  top speed {float(stat_speed[i]):.2f} (measured {top_speed[i]:.2f}) px/tick  -> planner {row['jump_tiles']}")
    TABLE.write_text(json.dumps(table, indent=1))
    (OUT / "gear_measure.json").write_text(json.dumps(table, indent=1))
    print(f"wrote {TABLE}")


if __name__ == "__main__":
    main()
