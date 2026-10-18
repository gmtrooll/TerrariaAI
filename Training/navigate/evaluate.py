"""Evaluate a Navigate policy on the held-out worlds with a fixed scenario list (the same list for every policy).

    python -m navigate.evaluate --run parity_custom              # trained model of a run (custom or SB3)
    python -m navigate.evaluate --policy greedy --name greedy    # scripted baseline
    python -m navigate.evaluate --policy random --name random

Each scenario is run once. Writes results/training/<run or name>/eval.json: success rate overall, per world, per
distance band and per layer, plus mean episode length of wins.
"""

import argparse
import collections
import json
import random
from pathlib import Path

import numpy as np
import torch

from navigate.env import BANDS, Curriculum, NavigateVecEnv
from navigate.model import ActorCritic, make_encoder
from terraria_mcp import scenarios as scenario_files

RESULTS = Path(__file__).resolve().parents[2] / "results" / "training"
EVAL_WORLDS = ["eval_01", "eval_02"]


def fixed_lists(worlds, per_band: int, seed: int = 12345) -> dict:
    rng = random.Random(seed)
    out = {}
    for w in worlds:
        rows = scenario_files.load(w)
        for i, s in enumerate(rows):
            s["id"] = f"{w}:{i}"
        chosen = []
        for band in BANDS:
            pool = [s for s in rows if s["band"] == band]
            chosen += rng.sample(pool, min(per_band, len(pool)))
        rng.shuffle(chosen)
        out[w] = chosen
    return out


def greedy_policy(spec):
    names = spec["self_names"] + spec["capability_names"] + spec["goal_names"]
    ix, iy, ivx = names.index("dir_x"), names.index("dir_y"), names.index("vx")
    rng = np.random.default_rng(0)

    def act(obs):
        v = obs["vec"]
        h = np.where(v[:, ix] > 0.15, 2, np.where(v[:, ix] < -0.15, 0, 1))
        jump = ((v[:, iy] < -0.25) | (np.abs(v[:, ivx]) < 0.05) | (rng.random(len(v)) < 0.1)).astype(int)
        vert = np.where(v[:, iy] > 0.6, 2, 1)
        return np.stack([h, vert, jump], 1)
    return act


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--run", default=None, help="training run whose model to evaluate")
    p.add_argument("--policy", choices=["model", "greedy", "random"], default="model")
    p.add_argument("--name", default=None, help="output folder for baselines")
    p.add_argument("--worlds", nargs="+", default=EVAL_WORLDS)
    p.add_argument("--per-band", type=int, default=64, help="scenarios per band per world")
    p.add_argument("--bots", type=int, default=32)
    p.add_argument("--stochastic", action="store_true", help="sample actions instead of taking the most likely")
    p.add_argument("--port", type=int, default=5690)
    args = p.parse_args()

    run_dir = RESULTS / (args.run or args.name)
    config = json.loads((run_dir / "config.json").read_text()) if args.run else {}
    obs_spec = config.get("obs", "grid=fovea,visibility=exposed")
    lists = fixed_lists(args.worlds, args.per_band)
    total = sum(len(v) for v in lists.values())
    env = NavigateVecEnv(args.worlds, bots_per_server=args.bots, port=args.port, obs=obs_spec,
                         curriculum=Curriculum(fixed=True), fixed_scenarios=lists)
    try:
        if args.policy == "model":
            device = "cuda"
            if config.get("algo") == "sb3":
                from stable_baselines3 import PPO as SB3PPO
                sb3 = SB3PPO.load(run_dir / "sb3_model", device=device)
                act = lambda obs: sb3.predict(obs, deterministic=not args.stochastic)[0]  # noqa: E731
            else:
                encoder = make_encoder(env.spec, env.fine_shape, env.coarse_shape, env.float_count, config.get("stride", 1))
                model = ActorCritic(encoder).to(device)
                model.load_state_dict(torch.load(run_dir / "model.pt", map_location=device)["model"])
                model.eval()

                def act(obs):
                    with torch.no_grad():
                        t = {k: torch.as_tensor(v).to(device) for k, v in obs.items()}
                        a, _, _ = model.act(t, deterministic=not args.stochastic)
                    return a.cpu().numpy()
        elif args.policy == "greedy":
            act = greedy_policy(env.spec)
        else:
            rng = np.random.default_rng(0)
            act = lambda obs: rng.integers(0, [3, 3, 2], size=(env.num_envs, 3))  # noqa: E731

        results = {}
        obs = env.reset()
        # The first scenario of every bot starts at connection, so run until every listed scenario has finished once.
        while len(results) < total:
            env.step_async(act(obs))
            obs, _, _, infos = env.step_wait()
            for info in infos:
                e = info.get("episode")
                if e and e["id"] not in results:
                    results[e["id"]] = e
        episodes = list(results.values())

        def summary(group):
            return {"n": len(group), "success": round(float(np.mean([e["win"] for e in group])), 4),
                    "died": round(float(np.mean([e["died"] for e in group])), 4),
                    "mean_win_length": round(float(np.mean([e["l"] for e in group if e["win"]])), 1) if any(e["win"] for e in group) else None}

        out = {"policy": args.policy, "run": args.run, "obs": obs_spec, "deterministic": not args.stochastic,
               "overall": summary(episodes)}
        for key in ("world", "band", "layer"):
            groups = collections.defaultdict(list)
            for e in episodes:
                groups[e[key]].append(e)
            out[f"by_{key}"] = {k: summary(v) for k, v in sorted(groups.items())}
        run_dir.mkdir(parents=True, exist_ok=True)
        (run_dir / "eval.json").write_text(json.dumps(out, indent=2))
        print(json.dumps({"overall": out["overall"], "by_band": out["by_band"]}, indent=1))
    finally:
        env.close()


if __name__ == "__main__":
    main()
