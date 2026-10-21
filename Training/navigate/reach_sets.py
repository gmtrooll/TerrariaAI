"""Route lengths of each world's main scenario set for every planner jump height in Training/loadouts.json (other
than 6 and 13, which have their own sets), so a loadout trains and tests on the scenarios its jumps can reach.
Writes Training/scenarios/<world>_reach.json: {"<jump tiles>": [route length per scenario, -1 = unreachable]}.

    python -m navigate.reach_sets --worlds train_01 ... eval_02
"""

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from terraria_mcp.game import GameManager  # noqa: E402
from terraria_mcp.scenarios import SCENARIO_DIR, load  # noqa: E402

from navigate.env import LOADOUT_ROWS  # noqa: E402

CODE = """var m = typeof(FirstMod_Sp.Common.Env.NavigateEnv).GetMethod("GetField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
var pairs = new[] {{ {pairs} }};
var jumps = new[] {{ {jumps} }};
var outp = new System.Collections.Generic.Dictionary<string, int[]>();
foreach (int j in jumps) outp[j.ToString()] = pairs.Select(q => ((PathDistanceField)m.Invoke(null, new object[] {{ q[2], q[3], j }})).GetDistance(q[0], q[1])).ToArray();
return outp;"""


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--worlds", nargs="+", required=True)
    p.add_argument("--port", type=int, default=7896)
    args = p.parse_args()
    jumps = sorted({r["jump_tiles"] for r in LOADOUT_ROWS.values()} - {6, 13})
    games = GameManager()
    for world in args.worlds:
        rows = load(world, "")
        pairs = ", ".join(f"new[] {{ {s['start_x']}, {s['start_y']}, {s['target_x']}, {s['target_y']} }}" for s in rows)
        name = f"reach_{world}"
        games.launch("server", world=world, name=name, bridge_port=args.port)
        try:
            out = games.call(name, "execute", timeout=3600, code=CODE.format(pairs=pairs, jumps=", ".join(map(str, jumps))))["result"]
        finally:
            games.stop(name)
        (SCENARIO_DIR / f"{world}_reach.json").write_text(json.dumps(out))
        print(world, {j: sum(1 for d in v if d >= 5) for j, v in out.items()}, f"of {len(rows)}", flush=True)


if __name__ == "__main__":
    main()
