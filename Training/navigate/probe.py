"""Probe a trained policy at one spot: start bots fresh at a given tile and compare with bots that arrive there
during their normal scenario (recurrent memory from the way there). Prints outcomes and the action
probabilities at the spot.

    python -m navigate.probe --run navigate_v1 --world eval_02 --spot 3869 396 --target 3857 409 --from 3887 416
"""

import argparse
import json

import numpy as np
import torch

from navigate.env import Curriculum, NavigateVecEnv
from navigate.evaluate import RESULTS, load_policy

H = ["left", "none", "right"]
V = ["up", "none", "down"]
J = ["no jump", "jump"]


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--run", required=True)
    p.add_argument("--world", required=True)
    p.add_argument("--spot", type=int, nargs=2, required=True, help="tile the bots get stuck on")
    p.add_argument("--target", type=int, nargs=2, required=True)
    p.add_argument("--from", dest="start", type=int, nargs=2, required=True, help="the original scenario's start")
    p.add_argument("--bots", type=int, default=16, help="per group")
    p.add_argument("--decisions", type=int, default=300)
    p.add_argument("--port", type=int, default=5785)
    args = p.parse_args()

    run_dir = RESULTS / args.run
    config = json.loads((run_dir / "config.json").read_text())
    def scenario(sx, sy, tag):
        return {"start_x": sx, "start_y": sy, "target_x": args.target[0], "target_y": args.target[1],
                "path_distance": 100, "band": tag, "layer": "-", "id": tag}
    rows = [scenario(*args.spot, "fresh") if i % 2 == 0 else scenario(*args.start, "original") for i in range(2 * args.bots)]
    env = NavigateVecEnv([args.world], bots_per_server=2 * args.bots, port=args.port, obs=config["obs"],
                         curriculum=Curriculum(fixed=True), fixed_scenarios={args.world: rows},
                         frame_skip=config.get("frame_skip", 4))
    try:
        policy = load_policy(run_dir, config, env, deterministic=False)
        names = env.spec["self_names"] + env.spec["capability_names"] + env.spec["goal_names"]
        idx = {n: names.index(n) for n in ("dx", "dy", "vx", "vy", "near_dx", "near_dy", "wp_dx", "wp_dy")}
        clip = 20.0  # ObservationBuilder.GoalClipTiles: offsets are tiles / 20
        obs = env.reset()
        ref = obs["vec"][0, [idx["dx"], idx["dy"]]].copy()  # target offset as seen from the spot (bot 0 starts there)
        steps = np.zeros(env.num_envs, int)  # decisions into the current episode
        results = {"fresh": [], "original": []}
        probs = {"fresh": [], "original": []}  # fresh: at the spot on an episode's first decision; original: arrived
        extra = {"fresh": [], "original": []}  # offset from the spot, vx, near waypoint and far waypoint (tiles)
        fresh = np.zeros(env.num_envs, bool)
        for t in range(args.decisions):
            with torch.no_grad():
                tens = {k: torch.as_tensor(v).to(policy.device) for k, v in obs.items()}
                logits, _, _ = policy.model.step(tens, policy.state, policy.starts)
            parts = [torch.softmax(x.float(), -1).cpu().numpy() for x in policy.model.split(logits)]
            off = (ref - obs["vec"][:, [idx["dx"], idx["dy"]]]) * clip  # bot centre relative to the spot, tiles
            at_spot = np.abs(off).max(1) < 0.7
            fresh = np.where(steps == 0, at_spot, fresh)
            for i in np.flatnonzero(at_spot):
                g = "fresh" if fresh[i] else "original"
                probs[g].append(np.concatenate([parts[0][i], parts[1][i], parts[2][i]]))
                v = obs["vec"][i]
                extra[g].append([off[i, 0], off[i, 1], v[idx["vx"]], v[idx["near_dx"]] * clip, v[idx["near_dy"]] * clip,
                                 v[idx["wp_dx"]] * clip, v[idx["wp_dy"]] * clip])
            actions = policy(obs)
            obs, _, dones, infos = env.step(actions)
            policy.observe(dones)
            steps = np.where(dones, 0, steps + 1)
            for i, info in enumerate(infos):
                if "episode" in info:  # the server queues scenarios, so groups are told apart by the episode's tag
                    results[info["episode"]["band"]].append(info["episode"]["win"])
            if min(len(r) for r in results.values()) >= args.bots:
                break
        for g in ("fresh", "original"):
            r = results[g]
            print(f"{g:9s}: reached {sum(r)}/{len(r)}")
            if probs[g]:
                m = np.mean(probs[g], 0)
                print(f"           {len(probs[g])} decisions at the spot; mean probabilities:")
                print("           horizontal " + ", ".join(f"{n} {v:.2f}" for n, v in zip(H, m[0:3])))
                print("           vertical   " + ", ".join(f"{n} {v:.2f}" for n, v in zip(V, m[3:6])))
                print("           jump       " + ", ".join(f"{n} {v:.2f}" for n, v in zip(J, m[6:8])))
                e = np.mean(extra[g], 0)
                print(f"           mean offset from spot ({e[0]:+.2f}, {e[1]:+.2f}) tiles, vx {e[2]:+.2f}, "
                      f"near waypoint ({e[3]:+.1f}, {e[4]:+.1f}), far waypoint ({e[5]:+.1f}, {e[6]:+.1f})")
    finally:
        env.close()


if __name__ == "__main__":
    main()
