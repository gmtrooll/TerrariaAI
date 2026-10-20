"""Compare the grid planner with and without consistentBody (PathDistanceField notes) on a world's scenarios:
how many route distances change, and how many scenarios become unreachable.

    python -m navigate.planner_check --world eval_02
"""

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from terraria_mcp.game import GameManager  # noqa: E402

SCENARIOS = Path(__file__).resolve().parents[1] / "scenarios"

CODE = """
var rows = System.IO.File.ReadAllLines(@"{path}");
var outp = new System.Collections.Generic.List<int[]>();
var cache = new System.Collections.Generic.Dictionary<(int, int, bool), PathDistanceField>();
foreach (var line in rows) {{
    var s = Newtonsoft.Json.Linq.JObject.Parse(line);
    int sx = (int)s["start_x"], sy = (int)s["start_y"], tx = (int)s["target_x"], ty = (int)s["target_y"];
    var d = new int[2];
    for (int k = 0; k < 2; k++) {{
        bool body = k == 1;
        if (!cache.TryGetValue((tx, ty, body), out var f)) {{
            f = PathDistanceField.Build(GridSensors.ClassifyPathTile, tx + 0.5f, ty - 0.3f, FirstMod_Sp.Common.Env.NavigateEnv.FieldRadius,
                {jump}, FirstMod_Sp.Common.Env.NavigateEnv.GoalRadiusTiles, FirstMod_Sp.Common.Env.NavigateEnv.MaxAirGapTiles,
                GridSensors.PathInfo, FirstMod_Sp.Common.Env.NavigateEnv.MaxSafeFallTiles, body);
            cache[(tx, ty, body)] = f;
        }}
        d[k] = f.GetDistance(sx, sy);
    }}
    outp.Add(d);
}}
return outp;
"""


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--world", required=True)
    p.add_argument("--jump", type=int, default=13, help="13 Tsunami, 6 no extra jump")
    args = p.parse_args()
    path = SCENARIOS / f"{args.world}.jsonl"
    games = GameManager()
    name = f"plannercheck_{args.world}"
    games.launch("server", world=args.world, name=name, bridge_port=7895)
    try:
        r = games.call(name, "execute", code=CODE.format(path=str(path), jump=args.jump), timeout=1200)["result"]
    finally:
        games.stop(name)
    rows = [json.loads(l) for l in path.read_text().splitlines()]
    out = Path(__file__).resolve().parents[2] / "results" / "training" / f"planner_check_{args.world}.json"
    out.write_text(json.dumps([{"id": f"{args.world}:{i}", "old": a, "new": b} for i, (a, b) in enumerate(r)]))
    changed = [(s, a, b) for s, (a, b) in zip(rows, r) if a != b]
    lost = [(s, a, b) for s, a, b in changed if b < 0]
    print(f"{args.world}: {len(rows)} scenarios, {len(changed)} with a different route length, {len(lost)} unreachable with consistentBody")
    for s, a, b in lost[:20]:
        print(f"   unreachable: start ({s['start_x']}, {s['start_y']}) target ({s['target_x']}, {s['target_y']}), was {a}")
    longer = sorted(((b - a, s) for s, a, b in changed if b >= 0), key=lambda t: -t[0])[:10]
    for dd, s in longer:
        print(f"   +{dd} tiles: start ({s['start_x']}, {s['start_y']}) target ({s['target_x']}, {s['target_y']})")


if __name__ == "__main__":
    main()
