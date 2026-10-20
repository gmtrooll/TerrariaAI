"""Evaluate a Navigate policy on the held-out worlds with a fixed scenario list (the same list for every policy).

    python -m navigate.evaluate --run parity_custom              # trained model of a run (custom or SB3)
    python -m navigate.evaluate --policy greedy --name greedy    # scripted baseline: walk toward the target
    python -m navigate.evaluate --policy greedy_wp --name greedy_wp  # scripted: walk toward the route waypoint
    python -m navigate.evaluate --policy random --name random

Each scenario is run once. Actions are sampled from the policy (as in training); --deterministic takes the most
likely action instead, which gets stuck far more often (Overnight Log, results 3). Writes results/training/<run or name>/eval.json: success rate overall, per world, per
distance band and per layer, plus mean episode length of wins.
"""

import argparse
import collections
import json
import random
from pathlib import Path

import numpy as np
import torch

from navigate.env import BANDS, LOADOUTS, Curriculum, NavigateVecEnv, detour_ratio
from navigate.model import ActorCritic, RecurrentActorCritic, make_encoder
from terraria_mcp import scenarios as scenario_files

RESULTS = Path(__file__).resolve().parents[2] / "results" / "training"
EVAL_WORLDS = ["eval_01", "eval_02"]


def fixed_lists(worlds, per_band: int, seed: int = 12345, loadout: str = "tsunami", min_detour: float = 0.0,
                scenario_set: str = "") -> dict:
    """per_band scenarios per band per world (fixed seed); with min_detour, instead every scenario whose route is at
    least that many times the straight-line distance."""
    rng = random.Random(seed)
    suffix, code = LOADOUTS[loadout]
    out = {}
    for w in worlds:
        rows = scenario_files.load(w, scenario_set or suffix)
        for i, s in enumerate(rows):
            s["id"] = f"{w}{scenario_set or suffix}:{i}"
            s["loadout"] = code
        if scenario_set:  # a separate test set (e.g. _bend): all of it
            out[w] = rows
            continue
        if min_detour > 0:
            out[w] = [s for s in rows if detour_ratio(s) >= min_detour]
            continue
        chosen = []
        for band in BANDS:
            pool = [s for s in rows if s["band"] == band]
            chosen += rng.sample(pool, min(per_band, len(pool)))
        rng.shuffle(chosen)
        out[w] = chosen
    return out


class Policy:
    """A trained run's policy: policy(obs) -> actions; policy.observe(dones) after every step (resets the memory
    of recurrent policies when an episode ended)."""

    def __init__(self, run_dir: Path, config: dict, env, deterministic: bool, device: str = "cuda", checkpoint: str = "model.pt"):
        self.deterministic = deterministic
        self.device = device
        self.sb3 = None
        if config.get("algo") == "sb3":
            from stable_baselines3 import PPO as SB3PPO
            self.sb3 = SB3PPO.load(run_dir / "sb3_model", device=device)
            return
        encoder = make_encoder(env.spec, env.fine_shape, env.coarse_shape, env.float_count, config.get("stride", 1))
        self.model = (RecurrentActorCritic(encoder) if config.get("recurrent") else ActorCritic(encoder)).to(device)
        self.model.load_state_dict(torch.load(run_dir / checkpoint, map_location=device, weights_only=False)["model"])
        self.model.eval()
        self.recurrent = getattr(self.model, "recurrent", False)
        if self.recurrent:
            self.state = self.model.initial_state(env.num_envs, device)
            self.starts = torch.ones(env.num_envs, device=device)

    def __call__(self, obs):
        if self.sb3 is not None:
            return self.sb3.predict(obs, deterministic=self.deterministic)[0]
        with torch.no_grad():
            t = {k: torch.as_tensor(v).to(self.device) for k, v in obs.items()}
            if self.recurrent:
                a, _, _, self.state = self.model.act(t, self.state, self.starts, deterministic=self.deterministic)
                self.starts = torch.zeros_like(self.starts)
            else:
                a, _, _ = self.model.act(t, deterministic=self.deterministic)
        return a.cpu().numpy()

    def observe(self, dones):
        if self.sb3 is None and self.recurrent:
            self.starts = torch.as_tensor(np.asarray(dones, dtype=np.float32), device=self.device)


def load_policy(run_dir: Path, config: dict, env, deterministic: bool, checkpoint: str = "model.pt") -> Policy:
    return Policy(run_dir, config, env, deterministic, checkpoint=checkpoint)


def greedy_policy(spec, toward="target"):
    names = spec["self_names"] + spec["capability_names"] + spec["goal_names"]
    prefix = "wp_" if toward == "waypoint" else ""
    ix, iy, ivx = names.index(prefix + "dir_x"), names.index(prefix + "dir_y"), names.index("vx")
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
    p.add_argument("--policy", choices=["model", "greedy", "greedy_wp", "random"], default="model")
    p.add_argument("--name", default=None, help="output folder for baselines")
    p.add_argument("--worlds", nargs="+", default=EVAL_WORLDS)
    p.add_argument("--per-band", type=int, default=64, help="scenarios per band per world")
    p.add_argument("--bots", type=int, default=32)
    p.add_argument("--deterministic", action="store_true", help="take the most likely action instead of sampling")
    p.add_argument("--port", type=int, default=5690)
    p.add_argument("--tag", default="", help="written to eval_<tag>.json instead of eval.json")
    p.add_argument("--loadout", choices=sorted(LOADOUTS), default="tsunami", help="equipment variant to evaluate")
    p.add_argument("--checkpoint", default="model.pt", help="model file in the run folder, e.g. model_20M.pt")
    p.add_argument("--obs-extra", default="", help="observation settings added to the run's, e.g. los=1")
    p.add_argument("--set", default="", help="evaluate all scenarios of Training/scenarios/<world><set>.jsonl, e.g. _bend")
    p.add_argument("--min-detour", type=float, default=0.0, help="evaluate all scenarios with route/straight >= this instead")
    p.add_argument("--record", action="store_true", help="servers write failed episodes (path + planned route) to failures_<world>.jsonl")
    args = p.parse_args()

    run_dir = RESULTS / (args.run or args.name)
    config = json.loads((run_dir / "config.json").read_text()) if args.run else {}
    obs_spec = config.get("obs", "grid=fovea,visibility=exposed") + ("," + args.obs_extra if args.obs_extra else "")
    if args.loadout != "tsunami" and not args.tag:
        args.tag = args.loadout
    lists = fixed_lists(args.worlds, args.per_band, loadout=args.loadout, min_detour=args.min_detour, scenario_set=args.set)
    total = sum(len(v) for v in lists.values())
    extra = []
    if args.record:
        run_dir.mkdir(parents=True, exist_ok=True)
        for old in run_dir.glob("failures_*.jsonl"):
            old.unlink()
        extra = ["-envrecord", str(run_dir / "failures")]
    env = NavigateVecEnv(args.worlds, bots_per_server=args.bots, port=args.port, obs=obs_spec,
                         curriculum=Curriculum(fixed=True), fixed_scenarios=lists, extra_args=extra,
                         frame_skip=config.get("frame_skip", 4), loadouts=[args.loadout])
    try:
        if args.policy == "model":
            act = load_policy(run_dir, config, env, args.deterministic, args.checkpoint)
        elif args.policy in ("greedy", "greedy_wp"):
            act = greedy_policy(env.spec, "waypoint" if args.policy == "greedy_wp" else "target")
        else:
            rng = np.random.default_rng(0)
            act = lambda obs: rng.integers(0, [3, 3, 2], size=(env.num_envs, 3))  # noqa: E731

        results = {}
        obs = env.reset()
        # Snags: decisions in a row pressing left or right while barely moving (|vx| < 0.3 px/tick: blocked); the
        # longest run per episode is saved with its outcome.
        ivx = (env.spec["self_names"] + env.spec["capability_names"] + env.spec["goal_names"]).index("vx")
        streak = np.zeros(env.num_envs, int)
        longest = np.zeros(env.num_envs, int)
        # The first scenario of every bot starts at connection, so run until every listed scenario has finished once.
        while len(results) < total:
            actions = act(obs)
            pressing = (np.asarray(actions)[:, 0] != 1) & (np.abs(obs["vec"][:, ivx]) < 0.03)
            streak = np.where(pressing, streak + 1, 0)
            longest = np.maximum(longest, streak)
            env.step_async(actions)
            obs, _, dones, infos = env.step_wait()
            if hasattr(act, "observe"):
                act.observe(dones)
            for i, info in enumerate(infos):
                e = info.get("episode")
                if e and e["id"] not in results:
                    e["snag"] = int(longest[i])
                    results[e["id"]] = e
            longest = np.where(dones, 0, longest)
            streak = np.where(dones, 0, streak)
        episodes = list(results.values())

        def summary(group):
            return {"n": len(group), "success": round(float(np.mean([e["win"] for e in group])), 4),
                    "died": round(float(np.mean([e["died"] for e in group])), 4),
                    "mean_win_length": round(float(np.mean([e["l"] for e in group if e["win"]])), 1) if any(e["win"] for e in group) else None}

        out = {"policy": args.policy, "run": args.run, "obs": obs_spec, "deterministic": args.deterministic, "worlds": args.worlds,
               "overall": summary(episodes)}
        # Per-scenario outcomes, for comparisons on subsets (e.g. routes where the planners disagree).
        out["episodes"] = [{"id": e["id"], "win": e["win"], "died": e["died"], "l": e["l"], "snag": e.get("snag", 0)} for e in episodes]
        for key in ("world", "band", "layer"):
            groups = collections.defaultdict(list)
            for e in episodes:
                groups[e[key]].append(e)
            out[f"by_{key}"] = {k: summary(v) for k, v in sorted(groups.items())}
        run_dir.mkdir(parents=True, exist_ok=True)
        (run_dir / (f"eval_{args.tag}.json" if args.tag else "eval.json")).write_text(json.dumps(out, indent=2))
        print(json.dumps({"overall": out["overall"], "by_band": out["by_band"]}, indent=1))
    finally:
        env.close()


if __name__ == "__main__":
    main()
