"""Custom PPO (docs/Custom Learner Design.md, v1).

Behaviour kept identical to Stable-Baselines3's PPO where possible, so the two can be compared on the same task:
clipped policy loss, unclipped value loss, entropy bonus, advantages from GAE normalised per minibatch, Adam with
eps 1e-5, gradient clipping, and timeouts bootstrapped with the value of the terminal observation. Speed options
(all off by default, switched on one at a time after the comparison):
  bf16      mixed precision for the network's forward and backward passes
  overlap   collect the next rollout with the previous weights while the update runs (policy lag of one update)
The rollout buffer lives on the GPU.

Recurrent models (model.recurrent, RecurrentActorCritic) keep a memory per bot that is reset when an episode starts
and carried over between rollouts. Their update trains on whole rollout sequences: each minibatch is batch_size /
n_steps bots x n_steps steps, unrolled from the memory each bot had when the rollout began.
"""

import copy
import threading
import time
from dataclasses import dataclass

import numpy as np
import torch
from torch import nn


@dataclass
class PPOConfig:
    n_steps: int = 128
    batch_size: int = 4096
    n_epochs: int = 4
    lr: float = 2.5e-4
    gamma: float = 0.99
    gae_lambda: float = 0.95
    clip: float = 0.2
    ent_coef: float = 0.01
    vf_coef: float = 0.5
    max_grad_norm: float = 0.5
    bf16: bool = False
    overlap: bool = False
    micro_batch: int = 0  # 0 = whole minibatch at once


class Rollout:
    def __init__(self, env, n_steps: int, device, memory: int = 0):
        n = env.num_envs
        self.obs = {k: torch.zeros((n_steps, n) + sp.shape, dtype=torch.uint8 if sp.dtype == np.uint8 else torch.float32, device=device)
                    for k, sp in env.observation_space.spaces.items()}
        self.actions = torch.zeros((n_steps, n, 3), dtype=torch.int64, device=device)
        self.logp = torch.zeros((n_steps, n), device=device)
        self.values = torch.zeros((n_steps, n), device=device)
        self.rewards = torch.zeros((n_steps, n), device=device)
        self.dones = torch.zeros((n_steps, n), device=device)
        self.last_value = torch.zeros(n, device=device)
        self.starts = torch.zeros((n_steps, n), device=device)          # recurrent: obs t starts an episode
        self.state0 = torch.zeros((n, memory), device=device) if memory else None
        self.episodes = []
        self.seconds = 0.0


class PPO:
    def __init__(self, env, model: nn.Module, config: PPOConfig, device: str = "cuda"):
        self.env = env
        self.model = model.to(device)
        self.cfg = config
        self.device = torch.device(device)
        self.optimizer = torch.optim.Adam(self.model.parameters(), lr=config.lr, eps=1e-5)
        self.actor = copy.deepcopy(self.model).eval() if config.overlap else self.model
        self.recurrent = getattr(model, "recurrent", False)
        memory = model.memory if self.recurrent else 0
        self.buffers = [Rollout(env, config.n_steps, self.device, memory) for _ in range(2 if config.overlap else 1)]
        self.obs = self._to_torch(env.reset())
        if self.recurrent:
            self.state = self.model.initial_state(env.num_envs, self.device)
            self.starts = torch.ones(env.num_envs, device=self.device)
        self.num_timesteps = 0

    def _to_torch(self, obs: dict) -> dict:
        return {k: torch.as_tensor(v).to(self.device, non_blocking=True) for k, v in obs.items()}

    def _autocast(self):
        return torch.autocast("cuda", dtype=torch.bfloat16, enabled=self.cfg.bf16)

    @torch.no_grad()
    def collect(self, actor: nn.Module, buf: Rollout):
        start = time.perf_counter()
        buf.episodes = []
        gamma = self.cfg.gamma
        if self.recurrent:
            buf.state0.copy_(self.state)
        for t in range(self.cfg.n_steps):
            with self._autocast():
                if self.recurrent:
                    actions, logp, value, h = actor.act(self.obs, self.state, self.starts)
                    buf.starts[t] = self.starts
                else:
                    actions, logp, value = actor.act(self.obs)
            for k in buf.obs:
                buf.obs[k][t].copy_(self.obs[k])
            buf.actions[t] = actions
            buf.logp[t] = logp
            buf.values[t] = value
            self.env.step_async(actions.cpu().numpy())
            next_obs, rewards, dones, infos = self.env.step_wait()
            rewards = torch.as_tensor(rewards, device=self.device)
            # Timeouts are not real terminal states: bootstrap with the value of the terminal observation.
            truncated = [i for i, info in enumerate(infos) if info.get("TimeLimit.truncated")]
            if truncated:
                term = {k: torch.as_tensor(np.stack([infos[i]["terminal_observation"][k] for i in truncated])).to(self.device)
                        for k in next_obs}
                with self._autocast():
                    if self.recurrent:
                        idx = torch.as_tensor(truncated, device=self.device)
                        _, _, tvalue, _ = actor.act(term, h[idx], torch.zeros(len(truncated), device=self.device))
                    else:
                        _, _, tvalue = actor.act(term)
                rewards[truncated] += gamma * tvalue
            buf.rewards[t] = rewards
            buf.dones[t] = torch.as_tensor(dones, device=self.device, dtype=torch.float32)
            buf.episodes.extend(info["episode"] for info in infos if "episode" in info)
            self.obs = self._to_torch(next_obs)
            if self.recurrent:
                self.state = h.float()
                self.starts = buf.dones[t].clone()
        with self._autocast():
            if self.recurrent:
                _, _, buf.last_value, _ = actor.act(self.obs, self.state, self.starts)
            else:
                _, _, buf.last_value = actor.act(self.obs)
        buf.last_value = buf.last_value.float()
        buf.seconds = time.perf_counter() - start

    def update(self, buf: Rollout) -> dict:
        start = time.perf_counter()
        cfg = self.cfg
        T, N = buf.rewards.shape
        # GAE. dones[t] = the episode ended after step t, so the value of obs t+1 does not continue it.
        adv = torch.zeros_like(buf.rewards)
        last = torch.zeros(N, device=self.device)
        for t in reversed(range(T)):
            next_value = buf.last_value if t == T - 1 else buf.values[t + 1]
            not_done = 1.0 - buf.dones[t]
            delta = buf.rewards[t] + cfg.gamma * next_value * not_done - buf.values[t]
            last = delta + cfg.gamma * cfg.gae_lambda * not_done * last
            adv[t] = last
        returns = adv + buf.values

        flat_obs = {k: v.reshape((T * N,) + v.shape[2:]) for k, v in buf.obs.items()}
        flat_actions = buf.actions.reshape(T * N, 3)
        flat_logp = buf.logp.reshape(-1)
        flat_adv = adv.reshape(-1)
        flat_ret = returns.reshape(-1)
        stats = {"pg_loss": [], "value_loss": [], "entropy": [], "approx_kl": [], "clip_fraction": []}
        self.model.train()
        if self.recurrent:
            self._update_sequences(buf, adv, returns, stats)
        for _ in range(0 if self.recurrent else cfg.n_epochs):
            perm = torch.randperm(T * N, device=self.device)
            for s in range(0, T * N, cfg.batch_size):
                idx = perm[s:s + cfg.batch_size]
                a_all = flat_adv[idx]
                a_all = (a_all - a_all.mean()) / (a_all.std() + 1e-8)
                self.optimizer.zero_grad(set_to_none=True)
                # Gradient accumulation over micro-batches (same update as one pass over the minibatch, less memory).
                micro = cfg.micro_batch or len(idx)
                for m in range(0, len(idx), micro):
                    sub = idx[m:m + micro]
                    a = a_all[m:m + micro]
                    mb_obs = {k: v[sub] for k, v in flat_obs.items()}
                    with self._autocast():
                        logp, entropy, value = self.model.evaluate(mb_obs, flat_actions[sub])
                    log_ratio = logp - flat_logp[sub]
                    ratio = log_ratio.exp()
                    pg_loss = -torch.min(ratio * a, ratio.clamp(1 - cfg.clip, 1 + cfg.clip) * a).mean()
                    value_loss = (flat_ret[sub] - value).pow(2).mean()
                    ent = entropy.mean()
                    loss = pg_loss + cfg.vf_coef * value_loss - cfg.ent_coef * ent
                    (loss * (len(sub) / len(idx))).backward()
                nn.utils.clip_grad_norm_(self.model.parameters(), cfg.max_grad_norm)
                self.optimizer.step()
                with torch.no_grad():
                    stats["pg_loss"].append(pg_loss.item())
                    stats["value_loss"].append(value_loss.item())
                    stats["entropy"].append(ent.item())
                    stats["approx_kl"].append(((ratio - 1) - log_ratio).mean().item())
                    stats["clip_fraction"].append(((ratio - 1).abs() > cfg.clip).float().mean().item())
        out = {k: float(np.mean(v)) for k, v in stats.items()}
        var = flat_ret.var()
        out["explained_variance"] = float(1 - (flat_ret - buf.values.reshape(-1)).var() / var) if var > 0 else 0.0
        out["update_seconds"] = time.perf_counter() - start
        return out

    def _update_sequences(self, buf: Rollout, adv, returns, stats):
        """Recurrent update: minibatches of whole bot sequences (T steps each), unrolled from buf.state0."""
        cfg = self.cfg
        T, N = buf.rewards.shape
        bots_per_batch = max(1, cfg.batch_size // T)
        bots_per_micro = max(1, (cfg.micro_batch or cfg.batch_size) // T)

        def take(x, bots):  # (T, N, ...) -> (T * B, ...) time-major for the chosen bots
            sel = x[:, bots]
            return sel.reshape((T * len(bots),) + sel.shape[2:])

        for _ in range(cfg.n_epochs):
            perm = torch.randperm(N, device=self.device)
            for s in range(0, N, bots_per_batch):
                bots = perm[s:s + bots_per_batch]
                a_all = adv[:, bots]
                a_all = (a_all - a_all.mean()) / (a_all.std() + 1e-8)
                self.optimizer.zero_grad(set_to_none=True)
                for m in range(0, len(bots), bots_per_micro):
                    sub = bots[m:m + bots_per_micro]
                    a = a_all[:, m:m + bots_per_micro].reshape(-1)
                    mb_obs = {k: take(v, sub) for k, v in buf.obs.items()}
                    with self._autocast():
                        logp, entropy, value = self.model.evaluate_sequences(mb_obs, take(buf.actions, sub), buf.state0[sub],
                                                                             buf.starts[:, sub])
                    old_logp = take(buf.logp, sub)
                    log_ratio = logp - old_logp
                    ratio = log_ratio.exp()
                    pg_loss = -torch.min(ratio * a, ratio.clamp(1 - cfg.clip, 1 + cfg.clip) * a).mean()
                    value_loss = (take(returns, sub) - value).pow(2).mean()
                    ent = entropy.mean()
                    loss = pg_loss + cfg.vf_coef * value_loss - cfg.ent_coef * ent
                    (loss * (len(sub) / len(bots))).backward()
                nn.utils.clip_grad_norm_(self.model.parameters(), cfg.max_grad_norm)
                self.optimizer.step()
                with torch.no_grad():
                    stats["pg_loss"].append(pg_loss.item())
                    stats["value_loss"].append(value_loss.item())
                    stats["entropy"].append(ent.item())
                    stats["approx_kl"].append(((ratio - 1) - log_ratio).mean().item())
                    stats["clip_fraction"].append(((ratio - 1).abs() > cfg.clip).float().mean().item())

    def learn(self, total_timesteps: int, on_iteration=None):
        """on_iteration(stats: dict, episodes: list) is called after every update."""
        per_iteration = self.cfg.n_steps * self.env.num_envs
        if not self.cfg.overlap:
            while self.num_timesteps < total_timesteps:
                buf = self.buffers[0]
                self.collect(self.model, buf)
                self.num_timesteps += per_iteration
                stats = self.update(buf)
                stats["collect_seconds"] = buf.seconds
                if on_iteration:
                    on_iteration(stats, buf.episodes)
            return

        # Overlapped: rollout k+1 is collected by the actor (weights after update k-1) while update k runs.
        self.collect(self.actor, self.buffers[0])
        self.num_timesteps += per_iteration
        current = 0
        while self.num_timesteps < total_timesteps:
            nxt = 1 - current
            collector = threading.Thread(target=self.collect, args=(self.actor, self.buffers[nxt]))
            collector.start()
            stats = self.update(self.buffers[current])
            collector.join()
            self.actor.load_state_dict(self.model.state_dict())
            self.num_timesteps += per_iteration
            stats["collect_seconds"] = self.buffers[nxt].seconds
            if on_iteration:
                on_iteration(stats, self.buffers[current].episodes)
            current = nxt

    def save(self, path):
        torch.save({"model": self.model.state_dict(), "optimizer": self.optimizer.state_dict(),
                    "num_timesteps": self.num_timesteps}, path)
