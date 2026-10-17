"""Upper bound for the PPO update on this GPU (part of M8): the project's feature extractor plus policy and value
heads, trained with a PPO-shaped loss on random minibatches in plain PyTorch, without Stable-Baselines3.
Compares fp32, TF32 and bf16 autocast, and channels_last. Reports samples/s for n_epochs passes, comparable to
bench_learner.py's update_steps_per_s.

    python bench_update_ceiling.py
"""

import time

import gymnasium as gym
import numpy as np
import torch
from torch import nn

from policy import TerrariaFeatureExtractor

GRID, STATS, ACTIONS = 40, 23, 8


class ActorCritic(nn.Module):
    def __init__(self):
        super().__init__()
        space = gym.spaces.Dict({
            "vision": gym.spaces.Box(0.0, 1.0, (1, GRID, GRID), np.float32),
            "stats": gym.spaces.Box(-2.0, 2.0, (STATS,), np.float32),
        })
        self.features = TerrariaFeatureExtractor(space)
        d = self.features.features_dim
        # SB3's MultiInputPolicy default: separate 64-64 MLPs for policy and value.
        self.pi = nn.Sequential(nn.Linear(d, 64), nn.Tanh(), nn.Linear(64, 64), nn.Tanh(), nn.Linear(64, ACTIONS))
        self.vf = nn.Sequential(nn.Linear(d, 64), nn.Tanh(), nn.Linear(64, 64), nn.Tanh(), nn.Linear(64, 1))

    def forward(self, vision, stats):
        f = self.features({"vision": vision, "stats": stats})
        return self.pi(f), self.vf(f).squeeze(-1)


def run(samples: int, batch: int, epochs: int, tf32: bool, bf16: bool, channels_last: bool) -> float:
    torch.backends.cuda.matmul.allow_tf32 = tf32
    torch.backends.cudnn.allow_tf32 = tf32
    torch.backends.cudnn.benchmark = True
    device = "cuda"
    model = ActorCritic().to(device)
    if channels_last:
        model = model.to(memory_format=torch.channels_last)
    opt = torch.optim.Adam(model.parameters(), lr=2e-4, eps=1e-5)
    vision = torch.randint(0, 3, (samples, 1, GRID, GRID), device=device).float() / 2
    if channels_last:
        vision = vision.contiguous(memory_format=torch.channels_last)
    stats = torch.rand(samples, STATS, device=device)
    actions = torch.randint(0, ACTIONS, (samples,), device=device)
    old_logp = torch.randn(samples, device=device) - 2
    adv = torch.randn(samples, device=device)
    ret = torch.randn(samples, device=device)

    def epoch():
        perm = torch.randperm(samples, device=device)
        for start in range(0, samples, batch):
            idx = perm[start:start + batch]
            with torch.autocast("cuda", dtype=torch.bfloat16, enabled=bf16):
                logits, value = model(vision[idx], stats[idx])
            dist = torch.distributions.Categorical(logits=logits.float())
            logp = dist.log_prob(actions[idx])
            ratio = (logp - old_logp[idx]).exp()
            a = adv[idx]
            pg = -torch.min(ratio * a, ratio.clamp(0.8, 1.2) * a).mean()
            vloss = (value.float() - ret[idx]).pow(2).mean()
            loss = pg + 0.5 * vloss - 0.01 * dist.entropy().mean()
            opt.zero_grad(set_to_none=True)
            loss.backward()
            nn.utils.clip_grad_norm_(model.parameters(), 0.5)
            opt.step()

    epoch()  # warmup (cuDNN autotune)
    torch.cuda.synchronize()
    t = time.perf_counter()
    for _ in range(epochs):
        epoch()
    torch.cuda.synchronize()
    return samples / (time.perf_counter() - t)  # samples/s for `epochs` passes, like an SB3 update


def main():
    samples, epochs = 131072, 4
    print(f"{torch.cuda.get_device_name(0)}, {samples} samples, {epochs} epochs")
    for batch in (512, 4096):
        for tf32, bf16, cl in ((False, False, False), (True, False, False), (True, True, False), (True, True, True)):
            sps = run(samples, batch, epochs, tf32, bf16, cl)
            label = "+".join([n for n, on in (("tf32", tf32), ("bf16", bf16), ("channels_last", cl)) if on]) or "fp32"
            print(f"batch {batch:5d} {label:24s} {sps:9.0f} samples/s", flush=True)


if __name__ == "__main__":
    main()
