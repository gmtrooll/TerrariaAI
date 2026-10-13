"""End-to-end: PPO + wrappers + feature extractor against the fake mod."""
import numpy as np
import pytest
import torch
from gymnasium import spaces

import protocol as P
from fake_terraria import FakeTerraria, free_port
from policy import TerrariaFeatureExtractor
import train_terraria as T


def _cfg(base_config, **kw):
    cfg = T.TrainingConfig(num_bots=2, batch_size=8, n_steps=8, n_epochs=1, device="cpu", seed=7,
                           checkpoint_interval=10_000, total_timesteps=64,
                           raw_config=dict(base_config, num_bots=2))
    for k, v in kw.items():
        setattr(cfg, k, v)
    return cfg


def _env(cfg, zone_db, tmp_path, max_steps=6):
    port = free_port()
    fake = FakeTerraria(port, cfg.num_bots, max_steps_per_zone=max_steps)
    fake.start()
    env = T.make_env(cfg, port=port, zone_database_path=zone_db, accept_timeout=10,
                     zone_stats_path=str(tmp_path / "zone_stats.json"))
    return env, fake


@pytest.mark.parametrize("frame_stack", [1, 3])
def test_ppo_learns_end_to_end(base_config, zone_db, tmp_path, frame_stack):
    cfg = _cfg(base_config, frame_stack=frame_stack)
    env, fake = _env(cfg, zone_db, tmp_path)
    try:
        model = T.build_model(env, cfg, tensorboard_log=None, verbose=0)
        assert isinstance(model.policy.features_extractor, TerrariaFeatureExtractor)
        metrics = {k: 0 for k in ("timesteps", "mean_reward", "mean_episode_length", "success_rate",
                                  "fps", "elapsed_time", "status")}
        metrics_cb = T.MetricsCallback(metrics)
        outcome_cb = T.EpisodeOutcomeCallback()
        model.learn(total_timesteps=64, callback=[outcome_cb, metrics_cb])
        assert model.num_timesteps >= 64
        # VecMonitor produced episode stats (episodes time out after 6 steps)
        assert len(model.ep_info_buffer) > 0
        assert {"r", "l", "is_success", "outcome"} <= set(model.ep_info_buffer[0])
        assert sum(outcome_cb.total_counts.values()) > 0
        assert metrics["mean_episode_length"] > 0
        assert env.observation_space["vision"].shape == (frame_stack, P.GRID_SIDE, P.GRID_SIDE)
        assert env.observation_space["stats"].shape == (P.STATS_SIZE * frame_stack,)

        path = str(tmp_path / "model")
        model.save(path)
        reloaded = T.build_model(env, cfg, path, tensorboard_log=None)
        with pytest.warns(UserWarning, match="reset"):
            obs = env.reset()  # second reset: episodes keep running, latest observation returned
        a1, _ = model.predict(obs, deterministic=True)
        a2, _ = reloaded.predict(obs, deterministic=True)
        np.testing.assert_array_equal(a1, a2)
    finally:
        env.close()
        fake.join(timeout=5)


def test_loading_old_layout_checkpoint_fails_clearly(base_config, zone_db, tmp_path):
    from stable_baselines3 import PPO
    from stable_baselines3.common.env_util import DummyVecEnv
    import gymnasium as gym

    class OldLayoutEnv(gym.Env):  # observation space of OBS_VERSION 1 (46 stats)
        observation_space = spaces.Dict({
            "vision": spaces.Box(0, 1, (1, 40, 40), np.float32),
            "stats": spaces.Box(-np.inf, np.inf, (46,), np.float32)})
        action_space = spaces.MultiBinary(3)

        def reset(self, seed=None, options=None):
            return self.observation_space.sample(), {}

    old = PPO("MultiInputPolicy", DummyVecEnv([OldLayoutEnv]), n_steps=8, batch_size=8, device="cpu",
              policy_kwargs=dict(normalize_images=False))
    path = str(tmp_path / "old_model")
    old.save(path)

    cfg = _cfg(base_config)
    env, fake = _env(cfg, zone_db, tmp_path)
    try:
        with pytest.raises(RuntimeError, match="older observation layout"):
            T.build_model(env, cfg, path, tensorboard_log=None)
    finally:
        env.close()
        fake.join(timeout=5)


def test_feature_extractor_keeps_spatial_detail():
    space = spaces.Dict({
        "vision": spaces.Box(0, 1, (2, P.GRID_SIDE, P.GRID_SIDE), np.float32),
        "stats": spaces.Box(-np.inf, np.inf, (P.STATS_SIZE * 2,), np.float32)})
    fx = TerrariaFeatureExtractor(space)
    out = fx({"vision": torch.zeros(5, 2, 40, 40), "stats": torch.zeros(5, P.STATS_SIZE * 2)})
    assert out.shape == (5, fx.features_dim)
    # last conv map is 10x10, not NatureCNN's 1x1
    conv_out = fx.cnn[:-1](torch.zeros(1, 2, 40, 40))
    assert conv_out.shape[-2:] == (10, 10)


def test_seeded_models_are_identical(base_config, zone_db, tmp_path):
    cfg = _cfg(base_config)
    params = []
    for _ in range(2):
        env, fake = _env(cfg, zone_db, tmp_path)
        try:
            model = T.build_model(env, cfg, tensorboard_log=None, verbose=0)
            params.append(torch.cat([p.flatten() for p in model.policy.parameters()]).detach().clone())
        finally:
            env.close()
            fake.join(timeout=5)
    assert torch.equal(params[0], params[1])


def test_cli_flag_names():
    args = T.parse_args(["--num-bots", "8"])
    assert args.num_bots == 8
    with pytest.warns(UserWarning, match="--num-bots"):
        args = T.parse_args(["--batch-size", "4"])
    assert args.num_bots == 4
