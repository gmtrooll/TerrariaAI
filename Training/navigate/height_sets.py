"""Scenarios that need more jump height than Tsunami in a Bottle gives: planned with a J-tile jump and unreachable
with 13 tiles (Oct 21: in the main sets no route needs more than 13, so higher-jumping gear never had to use its
height). Generated in the existing world (the world is not regenerated: template vaults stay). Writes
Training/scenarios/<world>_h<J>.jsonl; train and test with the loadout whose planner jump is J.

    python -m navigate.height_sets --worlds eval_01 eval_02 --jumps 20 25 36 59
"""

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from terraria_mcp.game import GameManager  # noqa: E402
from terraria_mcp.scenarios import BANDS, LAYERS, SCENARIO_DIR  # noqa: E402

D13 = """var m = typeof(FirstMod_Sp.Common.Env.NavigateEnv).GetMethod("GetField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
var pairs = new[] {{ {pairs} }};
return pairs.Select(q => ((PathDistanceField)m.Invoke(null, new object[] {{ q[2], q[3], 13 }})).GetDistance(q[0], q[1])).ToArray();"""


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--worlds", nargs="+", required=True)
    p.add_argument("--jumps", nargs="+", type=int, required=True)
    p.add_argument("--candidates", type=int, default=60, help="generated per layer and band, before the 13-tile filter")
    p.add_argument("--seed", type=int, default=7000)
    p.add_argument("--port", type=int, default=7898)
    args = p.parse_args()
    games = GameManager()
    for wi, world in enumerate(args.worlds):
        name = f"height_{world}"
        games.launch("server", world=world, name=name, bridge_port=args.port)
        try:
            for jump in args.jumps:
                cands = []
                for li, layer in enumerate(LAYERS):
                    for bi, (lo, hi) in enumerate(BANDS):
                        r = games.call(name, "generate_scenarios", timeout=900, count=args.candidates, min_distance=lo, max_distance=hi,
                                       layer=layer, seed=args.seed + wi * 1000 + jump * 10 + li * 4 + bi, max_jump=jump, max_air_gap=5,
                                       radius=max(hi + 10, 40))
                        for s in r["scenarios"]:
                            s.pop("path", None)
                            s.update(world=world, band=f"{lo}-{hi}", max_jump=jump, max_air_gap=5)
                            cands.append(s)
                d13 = []
                for k in range(0, len(cands), 500):  # batches: a long result comes back as text
                    pairs = ", ".join(f"new[] {{ {s['start_x']}, {s['start_y']}, {s['target_x']}, {s['target_y']} }}" for s in cands[k:k + 500])
                    d13 += games.call(name, "execute", timeout=1800, code=D13.format(pairs=pairs))["result"]
                rows = [s for s, d in zip(cands, d13) if d < 0]
                (SCENARIO_DIR / f"{world}_h{jump}.jsonl").write_text("".join(json.dumps(s) + "\n" for s in rows), encoding="utf-8")
                by_band = {b: sum(1 for s in rows if s["band"] == b) for b in (f"{lo}-{hi}" for lo, hi in BANDS)}
                print(f"{world} jump {jump}: {len(cands)} planned, {len(rows)} need more than 13 tiles {by_band}", flush=True)
        finally:
            games.stop(name)


if __name__ == "__main__":
    main()
