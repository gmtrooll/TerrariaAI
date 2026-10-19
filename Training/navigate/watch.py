"""Watch a trained Navigate policy in a game window, at normal game speed.

    python -m navigate.watch --run stride2                      # bots run held-out scenarios, camera on bot 1
    python -m navigate.watch --run stride2 --band 60-120        # only long routes
    python -m navigate.watch --run stride2 --mode follow        # the target is your own character: walk around

In the window: keys 1-9 put the camera on a bot, 0 back on your own character. Yellow sparkles mark each bot's
target, blue ones its route waypoint (12 path tiles ahead). The top of the screen shows the running tally. In follow
mode your character (the "Automation" player) is the target; move it with the keyboard as usual. Enemies are off,
as in training. Close the window or press Ctrl+C here to stop.
"""

import argparse
import json
import random

from navigate.env import BANDS, Curriculum, NavigateVecEnv
from navigate.evaluate import EVAL_WORLDS, RESULTS, load_policy
from terraria_mcp import scenarios as scenario_files

FOLLOW = {"start_x": -1, "start_y": -1, "target_x": -1, "target_y": -1, "path_distance": 100000,
          "band": "follow", "layer": "-", "id": "follow"}


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--run", required=True, help="training run whose model to watch")
    p.add_argument("--mode", choices=["scenarios", "follow"], default="scenarios")
    p.add_argument("--world", default=EVAL_WORLDS[0], help="world (default: a held-out one)")
    p.add_argument("--band", choices=BANDS, default=None, help="only scenarios of this distance band")
    p.add_argument("--bots", type=int, default=4)
    p.add_argument("--deterministic", action="store_true", help="most likely action instead of sampling")
    p.add_argument("--port", type=int, default=5790)
    p.add_argument("--checkpoint", default="model.pt", help="model file in the run folder, e.g. ckpt_30M.pt")
    args = p.parse_args()

    run_dir = RESULTS / args.run
    config = json.loads((run_dir / "config.json").read_text())
    if args.mode == "follow":
        lists = {args.world: [FOLLOW]}
    else:
        rows = scenario_files.load(args.world)
        for i, s in enumerate(rows):
            s["id"] = f"{args.world}:{i}"
        rows = [s for s in rows if args.band is None or s["band"] == args.band]
        random.Random().shuffle(rows)
        lists = {args.world: rows}

    print(f"launching a game window on {args.world} with {args.bots} bots running '{args.run}' ...", flush=True)
    env = NavigateVecEnv([args.world], bots_per_server=args.bots, port=args.port, obs=config.get("obs", "grid=fovea,visibility=exposed"),
                         curriculum=Curriculum(fixed=True), fixed_scenarios=lists, mode="client", bot_lean=False,
                         extra_args=["-envrealtime", "-envcamera", "-1" if args.mode == "follow" else "0"],
                         frame_skip=config.get("frame_skip", 4))
    try:
        act = load_policy(run_dir, config, env, args.deterministic, args.checkpoint)
        print("running; close the game window or press Ctrl+C to stop", flush=True)
        obs = env.reset()
        wins = total = 0
        while True:
            env.step_async(act(obs))
            obs, _, dones, infos = env.step_wait()
            act.observe(dones)
            for i, info in enumerate(infos):
                e = info.get("episode")
                if not e or args.mode == "follow":
                    continue
                total += 1
                wins += e["win"]
                result = "reached" if e["win"] else "died" if e["died"] else "timed out"
                print(f"bot {i + 1}: {result:9s} {e['band']:>6s} {e['layer']:<11s} route {e['path_distance']:3d} tiles, "
                      f"{e['l']:3d} decisions   scenario {e['id']}   (total {wins}/{total} = {100 * wins // total}%)", flush=True)
    except (KeyboardInterrupt, ConnectionError, OSError):
        pass
    finally:
        env.close()


if __name__ == "__main__":
    main()
