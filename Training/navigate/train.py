"""Train a Navigate policy with the custom PPO or with Stable-Baselines3 PPO on the same environment and network.

    python -m navigate.train --run custom_fovea --algo custom --steps 10000000
    python -m navigate.train --run sb3_fovea --algo sb3 --steps 10000000

Writes results/training/<run>/: config.json, progress.csv (one row per PPO iteration), scenario_stats.json
(attempts and wins per scenario, to find unreachable ones) and model.pt checkpoints.
Run from the Training folder.
"""

import argparse
import csv
import json
import time
from pathlib import Path

import numpy as np
import torch
from torch import nn

from navigate.env import BANDS, Curriculum, NavigateVecEnv, RewardConfig
from navigate.model import ActorCritic, RecurrentActorCritic, make_encoder
from navigate.ppo import PPO, PPOConfig

RESULTS = Path(__file__).resolve().parents[2] / "results" / "training"
TRAIN_WORLDS = ["train_01", "train_02", "train_03", "train_04", "train_05", "train_06"]


class ProgressLog:
    """One CSV row per iteration, the same columns for both learners."""

    FIELDS = ["iteration", "timesteps", "seconds", "steps_per_s", "episodes", "win_rate", "death_rate", "timeout_rate",
              "mean_return", "mean_length"] + [f"win_{b}" for b in BANDS] + ["open_bands", "pg_loss", "value_loss",
              "entropy", "approx_kl", "clip_fraction", "explained_variance", "collect_seconds", "update_seconds", "epochs"]

    def __init__(self, run_dir: Path, env: NavigateVecEnv):
        self.env = env
        self.path = run_dir / "progress.csv"
        self.file = open(self.path, "w", newline="", encoding="utf-8")
        self.writer = csv.DictWriter(self.file, fieldnames=self.FIELDS)
        self.writer.writeheader()
        self.start = time.time()
        self.iteration = 0
        self.last_steps = 0
        self.last_time = self.start

    def write(self, timesteps: int, stats: dict, episodes: list):
        self.iteration += 1
        now = time.time()
        row = {"iteration": self.iteration, "timesteps": timesteps, "seconds": round(now - self.start, 1),
               "steps_per_s": round((timesteps - self.last_steps) / max(1e-6, now - self.last_time), 1),
               "episodes": len(episodes)}
        self.last_steps, self.last_time = timesteps, now
        if episodes:
            row["win_rate"] = round(np.mean([e["win"] for e in episodes]), 4)
            row["death_rate"] = round(np.mean([e["died"] for e in episodes]), 4)
            row["timeout_rate"] = round(np.mean([e["timeout"] for e in episodes]), 4)
            row["mean_return"] = round(np.mean([e["r"] for e in episodes]), 3)
            row["mean_length"] = round(np.mean([e["l"] for e in episodes]), 1)
        for band, rate in self.env.curriculum.success().items():
            row[f"win_{band}"] = None if rate is None else round(rate, 4)
        row["open_bands"] = self.env.curriculum.open
        for k in ("pg_loss", "value_loss", "entropy", "approx_kl", "clip_fraction", "explained_variance", "collect_seconds", "update_seconds", "epochs"):
            if k in stats:
                row[k] = round(float(stats[k]), 5)
        self.writer.writerow(row)
        self.file.flush()
        print(f"it {self.iteration} steps {timesteps} sps {row['steps_per_s']} win {row.get('win_rate')} "
              f"bands {row['open_bands']} {[row[f'win_{b}'] for b in BANDS]}", flush=True)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--run", required=True)
    p.add_argument("--algo", choices=["custom", "sb3"], default="custom")
    p.add_argument("--worlds", nargs="+", default=TRAIN_WORLDS)
    p.add_argument("--bots", type=int, default=32, help="bots per server")
    p.add_argument("--obs", default="grid=fovea,visibility=exposed", help="observation settings (-obs)")
    p.add_argument("--stride", type=int, default=2, help="first convolution stride (2: same quality as 1, 31% faster; Overnight Log)")
    p.add_argument("--steps", type=int, default=10_000_000)
    p.add_argument("--n-steps", type=int, default=128)
    p.add_argument("--batch", type=int, default=4096)
    p.add_argument("--epochs", type=int, default=4)
    p.add_argument("--lr", type=float, default=2.5e-4)
    p.add_argument("--gamma", type=float, default=0.99)
    p.add_argument("--ent", type=float, default=0.01)
    p.add_argument("--bf16", action="store_true")
    p.add_argument("--overlap", action="store_true")
    p.add_argument("--micro-batch", type=int, default=0, help="gradient accumulation chunk (0 = whole minibatch)")
    p.add_argument("--recurrent", action="store_true", help="policy with memory (GRU); custom learner only")
    p.add_argument("--frame-skip", type=int, default=4, help="ticks per decision (time limits scale with it)")
    p.add_argument("--replay", type=float, default=0.0, help="share of episodes replaying hard scenarios (0 = off)")
    p.add_argument("--detour", type=float, default=0.0, help="share of episodes drawn from detour scenarios (0 = off)")
    p.add_argument("--categories", type=float, default=0.0, help="share of episodes drawn from the movement-category sets (0 = off)")
    p.add_argument("--damage-penalty", type=float, default=5.0, help="reward per full life lost (0 = off)")
    p.add_argument("--loadouts", default="tsunami", help="equipment variants, comma-separated: tsunami, none, or names from Training/loadouts.json")
    p.add_argument("--target-kl", type=float, default=0.0, help="stop an update early when the policy moves too far (0 = off)")
    p.add_argument("--physics-reward", action="store_true", help="progress reward from the physics movement graphs (option C)")
    p.add_argument("--port", type=int, default=5600)
    p.add_argument("--seed", type=int, default=0)
    p.add_argument("--checkpoint-every", type=int, default=50, help="iterations")
    args = p.parse_args()

    torch.manual_seed(args.seed)
    np.random.seed(args.seed)
    run_dir = RESULTS / args.run
    run_dir.mkdir(parents=True, exist_ok=True)
    (run_dir / "config.json").write_text(json.dumps(vars(args), indent=2))

    env = NavigateVecEnv(args.worlds, bots_per_server=args.bots, port=args.port, obs=args.obs,
                         curriculum=Curriculum(), seed=args.seed, frame_skip=args.frame_skip, replay=args.replay, detour=args.detour, categories=args.categories,
                         reward=RewardConfig(damage=args.damage_penalty,
                                             # physics distances drop in one step when a jump lands: no 3-tile cap
                                             max_progress_per_step=60.0 if args.physics_reward else 3.0),
                         loadouts=args.loadouts.split(","),
                         graph_dir=str(Path(__file__).resolve().parents[1] / "movegraphs") if args.physics_reward else None)
    log = ProgressLog(run_dir, env)
    try:
        coarse_shape = env.coarse_shape
        if args.algo == "custom":
            encoder = make_encoder(env.spec, env.fine_shape, coarse_shape, env.float_count, args.stride)
            model = RecurrentActorCritic(encoder) if args.recurrent else ActorCritic(encoder)
            cfg = PPOConfig(n_steps=args.n_steps, batch_size=args.batch, n_epochs=args.epochs, lr=args.lr,
                            gamma=args.gamma, ent_coef=args.ent, bf16=args.bf16, overlap=args.overlap,
                            micro_batch=args.micro_batch, target_kl=args.target_kl)
            ppo = PPO(env, model, cfg)

            milestone = [10_000_000]

            def on_iteration(stats, episodes):
                log.write(ppo.num_timesteps, stats, episodes)
                if log.iteration % args.checkpoint_every == 0:
                    ppo.save(run_dir / "model.pt")
                if ppo.num_timesteps >= milestone[0]:  # a kept copy every 10M steps, for comparisons at equal training
                    ppo.save(run_dir / f"model_{milestone[0] // 1_000_000}M.pt")
                    milestone[0] += 10_000_000

            ppo.learn(args.steps, on_iteration)
            ppo.save(run_dir / "model.pt")
        else:
            from stable_baselines3 import PPO as SB3PPO
            from stable_baselines3.common.callbacks import BaseCallback
            from navigate.model import SB3Encoder

            class LogCallback(BaseCallback):
                def __init__(self):
                    super().__init__()
                    self.episodes = []

                def _on_step(self):
                    for info in self.locals["infos"]:
                        if "episode" in info and "win" in info["episode"]:
                            self.episodes.append(info["episode"])
                    return True

                def _on_rollout_end(self):
                    stats = {k.split("/")[-1]: v for k, v in self.model.logger.name_to_value.items()}
                    stats["entropy"] = -stats.get("entropy_loss", 0.0)
                    log.write(self.num_timesteps, stats, self.episodes)
                    self.episodes = []
                    if log.iteration % args.checkpoint_every == 0:
                        self.model.save(run_dir / "sb3_model")

            policy_kwargs = dict(features_extractor_class=SB3Encoder,
                                 features_extractor_kwargs=dict(class_table=env.spec["classes"], first_stride=args.stride),
                                 net_arch=dict(pi=[256], vf=[256]), activation_fn=nn.ReLU, normalize_images=False)
            model = SB3PPO("MultiInputPolicy", env, n_steps=args.n_steps, batch_size=args.batch, n_epochs=args.epochs,
                           learning_rate=args.lr, gamma=args.gamma, ent_coef=args.ent, policy_kwargs=policy_kwargs,
                           device="cuda", seed=args.seed, verbose=0)
            model.learn(args.steps, callback=LogCallback())
            model.save(run_dir / "sb3_model")
        (run_dir / "scenario_stats.json").write_text(json.dumps(env.scenario_stats))
    finally:
        env.close()


if __name__ == "__main__":
    main()
