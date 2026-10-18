"""Navigate network: decodes the compact observation on the GPU and encodes it (docs/Observation Design.md).

Fine cell bytes -> channels: tile class features (lookup table from the mod's observation_spec), shape one-hot (6),
liquid amount + type one-hot (5), flag bits (wall, visible, remembered, locked door, outside: 5) = 38 channels.
Coarse cell bytes -> solid/platform/visible fractions (3) + 6 bits = 9 channels.
The same Encoder is used as SB3's features extractor and inside the custom PPO's ActorCritic, so the correctness
comparison uses identical networks.
"""

import gymnasium as gym
import numpy as np
import torch
from torch import nn
import torch.nn.functional as F

ACTION_SIZES = (3, 3, 2)  # horizontal, vertical, jump


def fine_channels(class_table: np.ndarray) -> int:
    return class_table.shape[1] + 6 + 5 + 5


class Encoder(nn.Module):
    def __init__(self, class_table, fine_shape, coarse_shape, vec_size, first_stride: int = 1,
                 fine_features: int = 256, coarse_features: int = 128, vec_features: int = 128):
        super().__init__()
        table = torch.as_tensor(np.asarray(class_table, dtype=np.float32))
        self.register_buffer("class_table", table, persistent=False)
        c = fine_channels(table)
        self.fine_cnn = nn.Sequential(
            nn.Conv2d(c, 32, 3, stride=first_stride, padding=1), nn.ReLU(),
            nn.Conv2d(32, 64, 3, stride=2, padding=1), nn.ReLU(),
            nn.Conv2d(64, 64, 3, stride=2, padding=1), nn.ReLU(),
            nn.Flatten(),
        )
        with torch.no_grad():
            n = self.fine_cnn(torch.zeros(1, c, fine_shape[1], fine_shape[2])).shape[1]
        self.fine_head = nn.Sequential(nn.Linear(n, fine_features), nn.ReLU())
        self.has_coarse = coarse_shape is not None
        out = fine_features
        if self.has_coarse:
            self.coarse_cnn = nn.Sequential(
                nn.Conv2d(9, 32, 3, stride=1, padding=1), nn.ReLU(),
                nn.Conv2d(32, 32, 3, stride=2, padding=1), nn.ReLU(),
                nn.Flatten(),
            )
            with torch.no_grad():
                m = self.coarse_cnn(torch.zeros(1, 9, coarse_shape[1], coarse_shape[2])).shape[1]
            self.coarse_head = nn.Sequential(nn.Linear(m, coarse_features), nn.ReLU())
            out += coarse_features
        self.vec_mlp = nn.Sequential(nn.Linear(vec_size, vec_features), nn.ReLU(), nn.Linear(vec_features, vec_features), nn.ReLU())
        out += vec_features
        self.features_dim = out
        self.register_buffer("bit_shifts", torch.arange(8, dtype=torch.int32), persistent=False)

    def decode_fine(self, fine: torch.Tensor) -> torch.Tensor:
        """fine: (B, 4, H, W) uint8 or float holding byte values -> (B, C, H, W) float."""
        fine = fine.to(torch.int64)
        cls = self.class_table[fine[:, 0].clamp(0, self.class_table.shape[0] - 1)]   # B,H,W,F
        shape = F.one_hot(fine[:, 1].clamp(0, 5), 6).float()                      # B,H,W,6
        liquid = fine[:, 2]
        amount = (liquid & 63).float() / 63.0
        kind = F.one_hot(liquid >> 6, 4).float() * (amount > 0).unsqueeze(-1).float()
        flags = ((fine[:, 3].unsqueeze(-1) >> self.bit_shifts[:5].to(torch.int64)) & 1).float()  # B,H,W,5
        x = torch.cat([cls, shape, amount.unsqueeze(-1), kind, flags], dim=-1)
        return x.permute(0, 3, 1, 2).contiguous()

    def decode_coarse(self, coarse: torch.Tensor) -> torch.Tensor:
        coarse = coarse.to(torch.int64)
        counts = coarse[:, [0, 1, 3]].float() / 16.0                               # solid, platform, visible
        bits = ((coarse[:, 2].unsqueeze(1) >> self.bit_shifts[:6].to(torch.int64).view(1, 6, 1, 1)) & 1).float()
        return torch.cat([counts, bits], dim=1)

    def forward(self, obs: dict) -> torch.Tensor:
        parts = [self.fine_head(self.fine_cnn(self.decode_fine(obs["fine"])))]
        if self.has_coarse:
            parts.append(self.coarse_head(self.coarse_cnn(self.decode_coarse(obs["coarse"]))))
        parts.append(self.vec_mlp(obs["vec"].float()))
        return torch.cat(parts, dim=1)


class ActorCritic(nn.Module):
    """Encoder + separate 256-unit policy and value layers + one categorical head per action type
    (the same shape as SB3's MultiInputPolicy with net_arch=dict(pi=[256], vf=[256]), ReLU, orthogonal init)."""

    def __init__(self, encoder: Encoder, hidden: int = 256):
        super().__init__()
        self.encoder = encoder
        d = encoder.features_dim
        self.pi = nn.Sequential(nn.Linear(d, hidden), nn.ReLU())
        self.vf = nn.Sequential(nn.Linear(d, hidden), nn.ReLU())
        self.action_head = nn.Linear(hidden, sum(ACTION_SIZES))
        self.value_head = nn.Linear(hidden, 1)
        self._init_weights()

    def _init_weights(self):
        for module in list(self.encoder.modules()) + list(self.pi.modules()) + list(self.vf.modules()):
            if isinstance(module, (nn.Linear, nn.Conv2d)):
                nn.init.orthogonal_(module.weight, gain=np.sqrt(2))
                nn.init.zeros_(module.bias)
        nn.init.orthogonal_(self.action_head.weight, gain=0.01)
        nn.init.zeros_(self.action_head.bias)
        nn.init.orthogonal_(self.value_head.weight, gain=1.0)
        nn.init.zeros_(self.value_head.bias)

    def forward(self, obs: dict):
        f = self.encoder(obs)
        logits = self.action_head(self.pi(f))
        value = self.value_head(self.vf(f)).squeeze(-1)
        return logits, value

    @staticmethod
    def split(logits: torch.Tensor):
        return torch.split(logits, ACTION_SIZES, dim=-1)

    def act(self, obs: dict, deterministic: bool = False):
        logits, value = self(obs)
        actions, logps = [], []
        for part in self.split(logits.float()):
            dist = torch.distributions.Categorical(logits=part)
            a = part.argmax(-1) if deterministic else dist.sample()
            actions.append(a)
            logps.append(dist.log_prob(a))
        return torch.stack(actions, -1), torch.stack(logps, -1).sum(-1), value.float()

    def evaluate(self, obs: dict, actions: torch.Tensor):
        logits, value = self(obs)
        logps, entropies = [], []
        for k, part in enumerate(self.split(logits.float())):
            dist = torch.distributions.Categorical(logits=part)
            logps.append(dist.log_prob(actions[:, k]))
            entropies.append(dist.entropy())
        return torch.stack(logps, -1).sum(-1), torch.stack(entropies, -1).sum(-1), value.float()


class RecurrentActorCritic(ActorCritic):
    """ActorCritic with memory: a GRU between the encoder and the policy/value layers, so the policy can remember
    what it tried (e.g. a failed jump) and do something else. The hidden state is reset when an episode starts.

    act(obs, state, starts) works step by step during collection; evaluate_sequences(...) unrolls stored rollouts
    (T steps x B bots) from the state the rollout started with, for the update."""

    recurrent = True

    def __init__(self, encoder: Encoder, hidden: int = 256, memory: int = 256):
        self.memory = memory
        super().__init__(encoder, hidden)
        d = encoder.features_dim
        self.gru = nn.GRUCell(d, memory)
        # The policy and value layers see the encoder features and the memory.
        self.pi = nn.Sequential(nn.Linear(d + memory, hidden), nn.ReLU())
        self.vf = nn.Sequential(nn.Linear(d + memory, hidden), nn.ReLU())
        self._init_weights()

    def initial_state(self, n: int, device) -> torch.Tensor:
        return torch.zeros(n, self.memory, device=device)

    def _heads(self, f, h):
        x = torch.cat([f, h.to(f.dtype)], dim=-1)
        return self.action_head(self.pi(x)), self.value_head(self.vf(x)).squeeze(-1)

    def step(self, obs: dict, state: torch.Tensor, starts: torch.Tensor):
        """One step for B bots. starts (B,) = 1 where obs is the first of an episode. Returns logits, value, state."""
        f = self.encoder(obs)
        h = self.gru(f.float(), state * (1.0 - starts).unsqueeze(-1))
        logits, value = self._heads(f, h)
        return logits, value, h

    def act(self, obs: dict, state: torch.Tensor = None, starts: torch.Tensor = None, deterministic: bool = False):
        n = obs["vec"].shape[0]
        if state is None:
            state = self.initial_state(n, obs["vec"].device)
        if starts is None:
            starts = torch.zeros(n, device=obs["vec"].device)
        logits, value, h = self.step(obs, state, starts)
        actions, logps = [], []
        for part in self.split(logits.float()):
            dist = torch.distributions.Categorical(logits=part)
            a = part.argmax(-1) if deterministic else dist.sample()
            actions.append(a)
            logps.append(dist.log_prob(a))
        return torch.stack(actions, -1), torch.stack(logps, -1).sum(-1), value.float(), h

    def evaluate_sequences(self, obs: dict, actions: torch.Tensor, state0: torch.Tensor, starts: torch.Tensor):
        """obs: dict of (T*B, ...) in time-major order, actions (T*B, 3), state0 (B, M), starts (T, B)."""
        T, B = starts.shape
        f = self.encoder(obs)                      # all steps at once
        f_t = f.view(T, B, -1)
        h = state0
        outs = []
        for t in range(T):
            h = self.gru(f_t[t].float(), h * (1.0 - starts[t]).unsqueeze(-1))
            outs.append(h)
        hs = torch.stack(outs).view(T * B, -1)
        logits, value = self._heads(f, hs)
        logps, entropies = [], []
        for k, part in enumerate(self.split(logits.float())):
            dist = torch.distributions.Categorical(logits=part)
            logps.append(dist.log_prob(actions[:, k]))
            entropies.append(dist.entropy())
        return torch.stack(logps, -1).sum(-1), torch.stack(entropies, -1).sum(-1), value.float()


def make_encoder(spec: dict, fine_shape, coarse_shape, vec_size, first_stride=1) -> Encoder:
    return Encoder(np.asarray(spec["classes"], dtype=np.float32), fine_shape, coarse_shape, vec_size, first_stride)


# ---------- SB3 adapter ----------

try:
    from stable_baselines3.common.torch_layers import BaseFeaturesExtractor

    class SB3Encoder(BaseFeaturesExtractor):
        """The same Encoder as an SB3 features extractor (normalize_images must be False)."""

        def __init__(self, observation_space: gym.spaces.Dict, class_table=None, first_stride: int = 1):
            fine = observation_space["fine"].shape
            coarse = observation_space["coarse"].shape if "coarse" in observation_space.spaces else None
            vec = observation_space["vec"].shape[0]
            enc = Encoder(np.asarray(class_table, dtype=np.float32), fine, coarse, vec, first_stride)
            super().__init__(observation_space, features_dim=enc.features_dim)
            self.enc = enc

        def forward(self, observations):
            return self.enc(observations)
except ImportError:  # SB3 is optional for the custom learner
    pass
