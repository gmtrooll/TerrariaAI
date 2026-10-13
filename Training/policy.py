"""
Feature extractor sized for the 40x40 tile grid.

SB3's default for image-like inputs is NatureCNN (8x8 stride 4, 4x4 stride 2, 3x3 stride 1),
built for 84x84 Atari frames; on a 40x40 grid it collapses the map to a 1x1 cell. Here small
3x3 kernels keep tile-level detail, and the stats vector gets its own MLP.
Works with frame stacking: the number of input channels is read from the observation space.
"""
import gymnasium as gym
import torch
from torch import nn
from stable_baselines3.common.torch_layers import BaseFeaturesExtractor


class TerrariaFeatureExtractor(BaseFeaturesExtractor):
    def __init__(self, observation_space: gym.spaces.Dict, cnn_features: int = 256, stats_features: int = 64):
        super().__init__(observation_space, features_dim=cnn_features + stats_features)
        vision_space = observation_space["vision"]
        stats_space = observation_space["stats"]
        channels = vision_space.shape[0]

        self.cnn = nn.Sequential(
            nn.Conv2d(channels, 32, kernel_size=3, stride=1, padding=1), nn.ReLU(),  # 40x40
            nn.Conv2d(32, 64, kernel_size=3, stride=2, padding=1), nn.ReLU(),        # 20x20
            nn.Conv2d(64, 64, kernel_size=3, stride=2, padding=1), nn.ReLU(),        # 10x10
            nn.Flatten(),
        )
        with torch.no_grad():
            n_flat = self.cnn(torch.zeros(1, *vision_space.shape)).shape[1]
        self.cnn_head = nn.Sequential(nn.Linear(n_flat, cnn_features), nn.ReLU())

        self.stats_mlp = nn.Sequential(
            nn.Linear(int(stats_space.shape[0]), stats_features), nn.ReLU(),
            nn.Linear(stats_features, stats_features), nn.ReLU(),
        )

    def forward(self, observations) -> torch.Tensor:
        vision = self.cnn_head(self.cnn(observations["vision"]))
        stats = self.stats_mlp(observations["stats"])
        return torch.cat([vision, stats], dim=1)


def policy_kwargs() -> dict:
    return dict(
        features_extractor_class=TerrariaFeatureExtractor,
        normalize_images=False,  # the grid is already 0 / 0.5 / 1
    )
