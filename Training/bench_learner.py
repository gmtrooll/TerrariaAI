"""Learner throughput (method M8): how many environment steps per second Stable-Baselines3 PPO can consume on this
machine with the project's policy, when the environment costs nothing.

A fake VecEnv returns pre-made observations of the real shape (40x40 grid + stats) instantly, so the measured time
is policy inference, SB3's per-step bookkeeping and the PPO update. Rollout and update time are measured
separately with a callback.

    python bench_learner.py --envs 16 64 256 512 --batch-size 512 2048
"""

import argparse
import csv
import time
from datetime import datetime
from pathlib import Path

import gymnasium as gym
import numpy as np
import torch
from stable_baselines3 import PPO
from stable_baselines3.common.callbacks import BaseCallback
from stable_baselines3.common.vec_env import VecEnv

from policy import policy_kwargs

STATS_SIZE = 23
GRID = 40
RESULTS = Path(__file__).resolve().parents[1] / "results" / "bench" / "learner.csv"


class FakeVecEnv(VecEnv):
    """Instant environment with the real observation and action spaces; episodes end at random (~1/500 steps)."""

    def __init__(self, num_envs: int, seed: int = 0):
        observation_space = gym.spaces.Dict({
            "vision": gym.spaces.Box(0.0, 1.0, (1, GRID, GRID), np.float32),
            "stats": gym.spaces.Box(-2.0, 2.0, (STATS_SIZE,), np.float32),
        })
        super().__init__(num_envs, observation_space, gym.spaces.Discrete(8))
        rng = np.random.default_rng(seed)
        self._pool = [{
            "vision": rng.choice([0.0, 0.5, 1.0], size=(num_envs, 1, GRID, GRID)).astype(np.float32),
            "stats": rng.uniform(-1, 1, size=(num_envs, STATS_SIZE)).astype(np.float32),
        } for _ in range(8)]
        self._rng = rng
        self._i = 0
        self._rewards = np.zeros(num_envs, np.float32)
        self._infos = [{} for _ in range(num_envs)]

    def _obs(self):
        self._i = (self._i + 1) % len(self._pool)
        return self._pool[self._i]

    def reset(self):
        return self._obs()

    def step_async(self, actions):
        pass

    def step_wait(self):
        dones = self._rng.random(self.num_envs) < 1 / 500
        return self._obs(), self._rewards, dones, self._infos

    def close(self):
        pass

    def get_attr(self, attr_name, indices=None):
        return [None] * self.num_envs

    def set_attr(self, attr_name, value, indices=None):
        pass

    def env_method(self, method_name, *args, indices=None, **kwargs):
        return [None] * self.num_envs

    def env_is_wrapped(self, wrapper_class, indices=None):
        return [False] * self.num_envs


class PhaseTimer(BaseCallback):
    """Wall time spent collecting rollouts versus updating, per PPO iteration."""

    def __init__(self):
        super().__init__()
        self.rollout_s, self.update_s = [], []
        self._t = None

    def _on_rollout_start(self):
        if self._t is not None:
            self.update_s.append(time.perf_counter() - self._t)  # time since the last rollout ended = update
        self._t = time.perf_counter()

    def _on_rollout_end(self):
        self.rollout_s.append(time.perf_counter() - self._t)
        self._t = time.perf_counter()

    def _on_step(self):
        return True


def run(envs: int, batch_size: int, n_steps: int, n_epochs: int, iterations: int, device: str) -> dict:
    env = FakeVecEnv(envs)
    model = PPO("MultiInputPolicy", env, n_steps=n_steps, batch_size=batch_size, n_epochs=n_epochs,
                learning_rate=2e-4, policy_kwargs=policy_kwargs(), device=device, verbose=0)
    timer = PhaseTimer()
    samples_per_iteration = envs * n_steps
    model.learn(total_timesteps=samples_per_iteration * (iterations + 1), callback=timer)
    # Drop the first iteration (CUDA/cuDNN warmup). The last update has no following rollout to close its timer.
    rollout = float(np.mean(timer.rollout_s[1:]))
    update = float(np.mean(timer.update_s[1:])) if len(timer.update_s) > 1 else float("nan")
    return {
        "time": datetime.now().isoformat(timespec="seconds"), "device": device, "envs": envs, "n_steps": n_steps,
        "batch_size": batch_size, "n_epochs": n_epochs, "samples_per_iteration": samples_per_iteration,
        "rollout_s": round(rollout, 3), "update_s": round(update, 3),
        "rollout_steps_per_s": round(samples_per_iteration / rollout),
        "update_steps_per_s": round(samples_per_iteration / update),
        "total_steps_per_s": round(samples_per_iteration / (rollout + update)),
        "gpu": torch.cuda.get_device_name(0) if device == "cuda" else "",
        "parameters": sum(p.numel() for p in model.policy.parameters()),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--envs", type=int, nargs="+", default=[16, 64, 256])
    parser.add_argument("--batch-size", type=int, nargs="+", default=[512])
    parser.add_argument("--n-steps", type=int, default=512)
    parser.add_argument("--n-epochs", type=int, default=4)
    parser.add_argument("--iterations", type=int, default=3)
    parser.add_argument("--device", default="cuda")
    args = parser.parse_args()

    RESULTS.parent.mkdir(parents=True, exist_ok=True)
    for envs in args.envs:
        for batch_size in args.batch_size:
            row = run(envs, batch_size, args.n_steps, args.n_epochs, args.iterations, args.device)
            new = not RESULTS.exists()
            with open(RESULTS, "a", newline="", encoding="utf-8") as f:
                writer = csv.DictWriter(f, fieldnames=list(row))
                if new:
                    writer.writeheader()
                writer.writerow(row)
            print(f"envs={envs} batch={batch_size}: rollout {row['rollout_steps_per_s']} steps/s, "
                  f"update {row['update_steps_per_s']} steps/s, total {row['total_steps_per_s']} steps/s "
                  f"({row['parameters']:,} params)", flush=True)


if __name__ == "__main__":
    main()
