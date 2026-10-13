import os
import sys

import pytest

TRAINING_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TESTS_DIR = os.path.dirname(os.path.abspath(__file__))
for p in (TRAINING_DIR, TESTS_DIR):
    if p not in sys.path:
        sys.path.insert(0, p)

from config_loader import get_default_config  # noqa: E402
from fake_terraria import FakeTerraria, free_port, write_zone_db  # noqa: E402


@pytest.fixture
def base_config():
    cfg = get_default_config()
    cfg.update(num_bots=3, seed=123, debug=False)
    return cfg


@pytest.fixture
def zone_db(tmp_path):
    # 1-D zones for the fake game: bots spawn at x=spawn and win at x=target.
    return write_zone_db(tmp_path / "zones.json", {
        "zone_a": (10.0, 0.0, 13.0, 0.0),
        "zone_b": (20.0, 0.0, 22.0, 0.0),
    })


@pytest.fixture
def make_env(tmp_path, base_config, zone_db):
    """Start a FakeTerraria client and a TerrariaVecEnv connected to it."""
    from terraria_env import TerrariaVecEnv
    created = []

    def _make(num_bots=3, fake_kwargs=None, env_kwargs=None, config=None):
        port = free_port()
        fake = FakeTerraria(port, (fake_kwargs or {}).pop("num_bots", num_bots), **(fake_kwargs or {}))
        fake.start()
        cfg = dict(base_config, num_bots=num_bots, **(config or {}))
        kwargs = dict(zone_database_path=zone_db, config=cfg, port=port, accept_timeout=10,
                      zone_stats_path=str(tmp_path / "zone_stats.json"))
        kwargs.update(env_kwargs or {})
        env = TerrariaVecEnv(num_envs=num_bots, **kwargs)
        created.append((env, fake))
        return env, fake

    yield _make
    for env, fake in created:
        env.close()
        fake.join(timeout=5)
