"""What a world's scenarios exercise: trace each scenario's planner route and count what it passes through (water,
honey, cobwebs, platforms, climbs, drops, reversals), to see how diverse the training scenarios are.

    python -m navigate.route_features --world train_01
Writes results/training/route_features_<world><suffix>.json (one row per scenario) and prints a summary.
"""

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from terraria_mcp.game import GameManager  # noqa: E402
from terraria_mcp.scenarios import SCENARIO_DIR  # noqa: E402

OUT = Path(__file__).resolve().parents[2] / "results" / "training"

CODE = r"""
var rows = System.IO.File.ReadAllLines(@"{path}");
var outp = new System.Collections.Generic.List<int[]>();
var cache = new System.Collections.Generic.Dictionary<(int, int), PathDistanceField>();
foreach (var line in rows) {{
    var s = Newtonsoft.Json.Linq.JObject.Parse(line);
    int x = (int)s["start_x"], y = (int)s["start_y"], tx = (int)s["target_x"], ty = (int)s["target_y"];
    if (!cache.TryGetValue((tx, ty), out var f)) {{
        f = PathDistanceField.Build(GridSensors.ClassifyPathTile, tx + 0.5f, ty - 0.3f, 130, {jump}, 1.5f, 5, GridSensors.PathInfo, 25, false);
        cache[(tx, ty)] = f;
    }}
    // water, honey, web, platform-drops, max climb run, max drop run, horizontal reversals, steps, lava-adjacent
    int water = 0, honey = 0, web = 0, platDrop = 0, climb = 0, maxClimb = 0, drop = 0, maxDrop = 0, rev = 0, steps = 0, lastDx = 0;
    for (int k = 0; k < 400; k++) {{
        byte info = 0;
        for (int j = 0; j < 3; j++) info |= GridSensors.PathInfo(x, y - j);
        if ((info & PathDistanceField.InfoWater) != 0) water++;
        if ((info & PathDistanceField.InfoHoney) != 0) honey++;
        if ((info & PathDistanceField.InfoWeb) != 0) web++;
        if (!f.Waypoint(x, y, 1, out int nx, out int ny) || (nx == x && ny == y)) break;
        steps++;
        int dx = nx - x, dy = ny - y;
        if (dy < 0) {{ climb++; maxClimb = Math.Max(maxClimb, climb); }} else climb = 0;
        if (dy > 0) {{ drop++; maxDrop = Math.Max(maxDrop, drop); if (GridSensors.ClassifyPathTile(x, y + 1) == PathTile.Platform) platDrop++; }} else drop = 0;
        if (dx != 0) {{ if (lastDx != 0 && dx != lastDx) rev++; lastDx = dx; }}
        x = nx; y = ny;
    }}
    outp.Add(new[] {{ water, honey, web, platDrop, maxClimb, maxDrop, rev, steps }});
}}
return outp;
"""
NAMES = ["water", "honey", "web", "platform_drops", "max_climb", "max_drop", "reversals", "steps"]


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--world", required=True)
    p.add_argument("--suffix", default="")
    p.add_argument("--jump", type=int, default=13)
    args = p.parse_args()
    path = SCENARIO_DIR / f"{args.world}{args.suffix}.jsonl"
    games = GameManager()
    name = f"features_{args.world}"
    games.launch("server", world=args.world, name=name, bridge_port=7893)
    try:
        r = games.call(name, "execute", code=CODE.format(path=str(path), jump=args.jump), timeout=1800)["result"]
    finally:
        games.stop(name)
    rows = [dict(zip(NAMES, v)) for v in r]
    (OUT / f"route_features_{args.world}{args.suffix}.json").write_text(json.dumps(rows))
    n = len(rows)
    print(f"{args.world}{args.suffix}: {n} scenarios")
    for label, test in [("passes water", lambda f: f["water"] > 0), ("passes honey", lambda f: f["honey"] > 0),
                        ("passes cobwebs", lambda f: f["web"] > 0), ("drops through a platform", lambda f: f["platform_drops"] > 0),
                        ("climbs 4+ tiles in a row", lambda f: f["max_climb"] >= 4), ("climbs 7+ (needs the double jump)", lambda f: f["max_climb"] >= 7),
                        ("drops 6+ tiles in a row", lambda f: f["max_drop"] >= 6), ("turns back horizontally at least once", lambda f: f["reversals"] >= 1),
                        ("turns back 3+ times", lambda f: f["reversals"] >= 3)]:
        k = sum(1 for f in rows if test(f))
        print(f"   {label:40s} {k:5d}  ({100 * k / n:.1f}%)")


if __name__ == "__main__":
    main()
