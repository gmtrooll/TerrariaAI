"""Obstacle templates for movement categories that natural worlds rarely give (docs/Navigate Progress.md, Oct 20: the
honey test comes from 23-27 pools per world, the honey block test from one hive). Each template is a sealed room (a
"vault") carved out of solid rock: no existing scenario's route can pass through rock, so the stored scenario sets
of the world stay valid, and a vault's own scenarios stay inside it. The parameters are drawn at random per vault.

  honey_pit    a pit of honey (3-12 wide, 1-6 deep) in the floor; the floor beside it raised 0-9 tiles on each side;
               often a ledge (solid or platform) 4-13 tiles above. Starts in the honey (target on a side or the
               ledge) or on one side (target on the other: jump the pit, or climb out after falling in).
               Physics: jumps from honey reach ~3 tiles, 10 / 6 with Tsunami (PathDistanceField.HoneyJumpTiles).
  honey_floor  a floor of honey blocks (run speed and acceleration x 0.25, jump 1.85 tiles, 8.8 with Tsunami) with
               a step (1-4), a wall (2-8 tall), a dry gap (3-6 wide, 3-5 deep) or a ledge (3-9 up) on it. Starts at one end, targets at the other or on the ledge.

    python -m navigate.templates --worlds train_01 train_02 --per-world 40 --seed 1
    python -m navigate.templates --worlds eval_01 eval_02 --per-world 30 --seed 900 --suffix _tmpl
Saves the world (a backup of the .wld goes to <training save dir>/WorldBackups, or TML_WORLD_BACKUPS, first) and writes the rows, which
carry category and band like the category sets, to Training/scenarios/<world><suffix>.jsonl (training reads
_tmpl rows along with _cat ones; evaluate with --set _tmpl).
"""

import argparse
import json
import os
import random
import shutil
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from terraria_mcp import config  # noqa: E402
from terraria_mcp.game import GameManager  # noqa: E402
from terraria_mcp.scenarios import SCENARIO_DIR  # noqa: E402

BACKUPS = Path(os.environ.get("TML_WORLD_BACKUPS", config.SAVE_DIR / "WorldBackups"))  # this PC: K:\FirstMod_Sp_data\world_backups
W, H = 36, 26        # vault box (tiles), including its rock shell (larger boxes: few sealed sites, 20 per world)
FLOOR_DEPTH = 8      # the floor row is H - FLOOR_DEPTH below the box top: pits up to 6 deep keep rock under them
MARGIN = 1           # solid rock required around the box (the shell is 2 tiles thick)
KINDS = ["honey_pit", "honey_floor"]


def band_of(d):
    return "5-15" if d < 15 else "15-30" if d < 30 else "30-60" if d < 60 else "60-120"


SITES_CODE = r"""
var rng = new System.Random({seed});
var found = new System.Collections.Generic.List<int[]>();
int yMin = (int)Main.worldSurface + 20, yMax = Main.UnderworldLayer - 40;
bool Rock(int x, int y) {{
    var t = Main.tile[x, y];
    return t.HasTile && Main.tileSolid[t.TileType] && !Main.tileSolidTop[t.TileType] && t.LiquidAmount == 0
        && !TileID.Sets.Falling[t.TileType] && t.TileType != TileID.LihzahrdBrick;
}}
for (int tries = 0; found.Count < {count} && tries < 400000; tries++) {{
    int x = rng.Next(120, Main.maxTilesX - 120 - {w}), y = rng.Next(yMin, yMax - {h});
    bool ok = true;
    foreach (var f in found) if (x < f[0] + {w} + 8 && f[0] < x + {w} + 8 && y < f[1] + {h} + 8 && f[1] < y + {h} + 8) {{ ok = false; break; }}
    // The shell (the box's outer tile and MARGIN more) must be rock: it seals the vault. Inside, at most 30% may be
    // open (enclosed pockets: the carving removes them), and no liquid.
    int open = 0;
    for (int i = x - {m}; ok && i < x + {w} + {m}; i++)
        for (int j = y - {m}; j < y + {h} + {m}; j++) {{
            bool inside = i > x && i < x + {w} - 1 && j > y && j < y + {h} - 1;
            if (Rock(i, j)) continue;
            if (!inside || Main.tile[i, j].LiquidAmount > 0 || ++open > {w} * {h} * 30 / 100) {{ ok = false; break; }}
        }}
    if (ok) found.Add(new[] {{ x, y }});
}}
return found;
"""

HELPERS = [
    "void Solid(int x0, int y0, int x1, int y1, ushort type) { for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) { var t = Main.tile[x, y]; t.ClearEverything(); t.ResetToType(type); } }",
    "void Air(int x0, int y0, int x1, int y1) { for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) { var t = Main.tile[x, y]; ushort wall = t.WallType; t.ClearEverything(); t.WallType = wall; } }",
    "void Liquid(int x0, int y0, int x1, int y1, int type) { for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) { var t = Main.tile[x, y]; t.LiquidType = type; t.LiquidAmount = 255; } }",
    "void Platform(int x0, int x1, int y) { for (int x = x0; x <= x1; x++) { var t = Main.tile[x, y]; t.ClearEverything(); t.ResetToType(TileID.Platforms); } }",
]


def honey_pit(rng, x0, y0):
    """Build lines and candidate (start, target, note) for a honey pit vault at box (x0, y0)."""
    F = y0 + H - FLOOR_DEPTH            # floor row: feet stand on F - 1
    inner0, inner1 = x0 + 1, x0 + W - 2
    pw, pd = rng.randint(3, 12), rng.randint(1, 6)
    px = rng.randint(inner0 + 8, inner1 - 8 - pw)
    hl, hr = rng.choice([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]), rng.choice([0, 1, 2, 3, 4, 5, 6, 7, 8, 9])
    lines = [f"Air({inner0}, {y0 + 1}, {inner1}, {F - 1});"]
    if hl:
        lines.append(f"Solid({inner0}, {F - hl}, {px - 1}, {F - 1}, TileID.Stone);")
    if hr:
        lines.append(f"Solid({px + pw}, {F - hr}, {inner1}, {F - 1}, TileID.Stone);")
    lines.append(f"Air({px}, {F}, {px + pw - 1}, {F + pd - 1}); Liquid({px}, {F}, {px + pw - 1}, {F + pd - 1}, LiquidID.Honey);")
    params = {"pit_width": pw, "pit_depth": pd, "side_left": hl, "side_right": hr}
    cands = []
    pit_x = rng.randint(px, px + pw - 2)
    left = (rng.randint(inner0 + 2, px - 2), F - hl - 1)
    right = (rng.randint(px + pw + 1, inner1 - 2), F - hr - 1)
    cands += [((pit_x, F + pd - 1), left, "out_left"), ((pit_x, F + pd - 1), right, "out_right")]
    cands += [(left, right, "across"), (right, left, "across")]
    if rng.random() < 0.6:
        ll, lh = rng.randint(3, 7), rng.randint(4, 13)
        lx = rng.randint(max(inner0 + 2, px - 3), min(inner1 - ll - 1, px + pw))
        row = F - lh
        if rng.random() < 0.3:
            lines.append(f"Platform({lx}, {lx + ll - 1}, {row});")
        else:
            lines.append(f"Solid({lx}, {row}, {lx + ll - 1}, {row}, TileID.Stone);")
        params.update(ledge_height=lh, ledge_length=ll)
        cands.append(((pit_x, F + pd - 1), (lx + ll // 2, row - 1), "out_ledge"))
    return lines, cands, params


def honey_floor(rng, x0, y0):
    F = y0 + H - FLOOR_DEPTH
    inner0, inner1 = x0 + 1, x0 + W - 2
    a, b = inner0 + rng.randint(0, 6), inner1 - rng.randint(0, 6)
    lines = [f"Air({inner0}, {y0 + 1}, {inner1}, {F - 1});",
             f"for (int x = {a}; x <= {b}; x++) Main.tile[x, {F}].ResetToType(TileID.HoneyBlock);"]
    feature = rng.choice(["step", "wall", "gap", "ledge", "flat"])
    params = {"feature": feature, "honey_from": a - x0, "honey_to": b - x0}
    mid = rng.randint(a + 8, b - 8)
    end_l, end_r = (a + 2, F - 1), (b - 2, F - 1)
    if feature == "step":
        s = rng.randint(1, 4)
        lines.append(f"Solid({mid}, {F - s}, {b}, {F - 1}, TileID.Stone); for (int x = {mid}; x <= {b}; x++) Main.tile[x, {F - s}].ResetToType(TileID.HoneyBlock);")
        end_r = (b - 2, F - s - 1)
        params["height"] = s
    elif feature == "wall":
        h, w = rng.randint(2, 8), rng.randint(1, 2)
        lines.append(f"Solid({mid}, {F - h}, {mid + w - 1}, {F - 1}, TileID.Stone);")
        params.update(height=h, width=w)
    elif feature == "gap":
        g, d = rng.randint(3, 6), rng.randint(3, 5)
        lines.append(f"Air({mid}, {F}, {mid + g - 1}, {F + d - 1});")
        params.update(width=g, depth=d)
    cands = [(end_l, end_r, "across"), (end_r, end_l, "across")]
    if feature == "ledge":
        ll, lh = rng.randint(3, 7), rng.randint(3, 9)
        lx = rng.randint(a + 3, b - ll - 3)
        lines.append(f"Solid({lx}, {F - lh}, {lx + ll - 1}, {F - lh}, TileID.Stone);")
        params.update(height=lh, length=ll)
        cands.append(((rng.choice([a + 2, b - 2]), F - 1), (lx + ll // 2, F - lh - 1), "up_ledge"))
    return lines, cands, params


BUILDERS = {"honey_pit": honey_pit, "honey_floor": honey_floor}


def stamp(world, per_world, seed, suffix, port):
    rng = random.Random(seed)
    games = GameManager()
    wld = config.WORLDS_DIR / f"{world}.wld"
    BACKUPS.mkdir(parents=True, exist_ok=True)
    stamp_s = time.strftime("%Y%m%d_%H%M%S")
    for ext in (".wld", ".twld"):
        src = wld.with_suffix(ext)
        if src.exists():
            shutil.copy2(src, BACKUPS / f"{world}_{stamp_s}{ext}")
    name = f"tmpl_{world}"
    games.launch("server", world=world, name=name, bridge_port=port)
    rows = []
    try:
        sites = games.call(name, "execute", timeout=600, code=SITES_CODE.format(seed=seed, count=per_world * 2, w=W, h=H, m=MARGIN))["result"]
        # no existing scenario may start or end inside a vault (an enclosed pocket the carving would change)
        ends = [(s[k + "_x"], s[k + "_y"]) for f in SCENARIO_DIR.glob(f"{world}*.jsonl") if not f.stem.endswith(suffix)
                for s in map(json.loads, f.read_text(encoding="utf-8").splitlines()) for k in ("start", "target")]
        print(f"{world}: {len(sites)} sealed sites before the scenario filter", flush=True)
        sites = [(x, y) for x, y in sites if not any(x - 1 <= ex <= x + W and y - 1 <= ey <= y + H for ex, ey in ends)][:per_world]
        print(f"{world}: {len(sites)} vault sites", flush=True)
        lines, cands = list(HELPERS), []
        for i, (x0, y0) in enumerate(sites):
            kind = KINDS[i % len(KINDS)]
            build, cs, params = BUILDERS[kind](rng, x0, y0)
            lines += build
            cands += [(kind, i, s, t, note, params) for s, t, note in cs]
        lines += [f"WorldGen.RangeFrame({x0 - 1}, {y0 - 1}, {x0 + W + 1}, {y0 + H + 1});" for x0, y0 in sites]
        lines.append("return \"built\";")
        games.call(name, "execute", timeout=600, code="\n".join(lines))
        pairs = ", ".join(f"new[] {{ {s[0]}, {s[1]}, {t[0]}, {t[1]} }}" for _, _, s, t, _, _ in cands)
        code = ("var m = typeof(FirstMod_Sp.Common.Env.NavigateEnv).GetMethod(\"GetField\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);\n"
                f"var pairs = new[] {{ {pairs} }};\n"
                "return pairs.Select(q => ((PathDistanceField)m.Invoke(null, new object[] { q[2], q[3], 13 })).GetDistance(q[0], q[1])).ToArray();")
        dist = games.call(name, "execute", timeout=900, code=code)["result"]
        layer_of = games.call(name, "execute", code="return new[] { (int)Main.worldSurface, (int)Main.rockLayer };")["result"]
        for (kind, i, s, t, note, params), d in zip(cands, dist):
            if d < 5:
                continue
            rows.append({"start_x": s[0], "start_y": s[1], "target_x": t[0], "target_y": t[1], "path_distance": int(d),
                         "straight_distance": round(((s[0] - t[0]) ** 2 + (s[1] - t[1]) ** 2) ** 0.5, 1), "height_delta": s[1] - t[1],
                         "layer": "underground" if t[1] < layer_of[1] else "cavern", "category": kind, "template": note,
                         "vault": i, "params": params, "world": world, "band": band_of(d), "max_jump": 13, "max_air_gap": 5})
        kept = {k: sum(1 for r in rows if r["category"] == k) for k in KINDS}
        print(f"{world}: {len(cands)} candidates, {len(rows)} reachable {kept}", flush=True)
    finally:
        games.stop(name, save=True)
    path = SCENARIO_DIR / f"{world}{suffix}.jsonl"
    path.write_text("".join(json.dumps(r) + "\n" for r in rows), encoding="utf-8")
    print(f"wrote {path}", flush=True)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--worlds", nargs="+", required=True)
    p.add_argument("--per-world", type=int, default=40)
    p.add_argument("--seed", type=int, default=1)
    p.add_argument("--suffix", default="_tmpl")
    p.add_argument("--port", type=int, default=7899)
    args = p.parse_args()
    for wi, world in enumerate(args.worlds):
        stamp(world, args.per_world, args.seed + 1000 * wi, args.suffix, args.port)


if __name__ == "__main__":
    main()
