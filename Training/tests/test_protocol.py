"""Env <-> fake mod: handshake, alignment, episode boundaries, disconnects."""
import socket

import numpy as np
import pytest

import protocol as P
from fake_terraria import FakeTerraria, free_port
from terraria_env import TerrariaVecEnv


def actions_for(bits_per_bot):
    return np.array([[(b >> 0) & 1, (b >> 1) & 1, (b >> 2) & 1] for b in bits_per_bot])


def test_handshake_rejects_num_bots_mismatch(base_config, zone_db):
    port = free_port()
    fake = FakeTerraria(port, num_bots=8)
    fake.start()
    with pytest.raises(P.HandshakeError, match=r"num_bots: mod=8 .* python=4"):
        TerrariaVecEnv(num_envs=4, zone_database_path=zone_db, config=base_config, port=port, accept_timeout=10)
    fake.join(timeout=5)
    # The mod gets a readable reason too
    assert fake.handshake_status == P.HANDSHAKE_REJECTED
    assert "num_bots" in fake.handshake_message


@pytest.mark.parametrize("override, expected", [
    ({"protocol_version": 1}, "protocol version"),
    ({"obs_version": 1}, "observation version"),
    ({"stats_size": 46}, "stats size"),
    ({"magic": 0x12345678}, "bad magic"),
])
def test_handshake_rejects_version_mismatch(base_config, zone_db, override, expected):
    port = free_port()
    fake = FakeTerraria(port, num_bots=3, **override)
    fake.start()
    with pytest.raises(P.HandshakeError, match=expected):
        TerrariaVecEnv(num_envs=3, zone_database_path=zone_db, config=base_config, port=port, accept_timeout=10)
    fake.join(timeout=5)


def test_handshake_rejects_wrong_world(base_config, zone_db):
    port = free_port()
    fake = FakeTerraria(port, num_bots=3, world_name="SomeOtherWorld")
    fake.start()
    with pytest.raises(P.HandshakeError, match="SomeOtherWorld"):
        TerrariaVecEnv(num_envs=3, zone_database_path=zone_db, config=base_config, port=port, accept_timeout=10)
    fake.join(timeout=5)


def test_reset_returns_real_first_observation(make_env):
    env, fake = make_env()
    obs = env.reset()
    # Packet 0 (not a zero dummy): every bot already stands in its first zone.
    assert np.all(obs["stats"][:, 1] == 0)
    assert np.all(obs["stats"][:, 2] > 0)
    assert obs["vision"].shape == (3, 1, P.GRID_SIDE, P.GRID_SIDE)


def test_observation_follows_the_action_just_sent(make_env):
    """B4: step(a_t) must return the state after a_t, not after a_{t-1}."""
    env, fake = make_env(fake_kwargs={"max_steps_per_zone": 10_000})
    env.reset()
    rng = np.random.default_rng(0)
    for t in range(1, 30):
        bits = rng.integers(0, 8, size=env.num_envs)
        obs, rewards, dones, infos = env.step(actions_for(bits))
        assert np.all(obs["stats"][:, 1] == t), "packet index"
        np.testing.assert_array_equal(obs["stats"][:, 0], bits)


def test_reward_matches_the_action_just_sent(make_env, base_config):
    """Moving towards the target earns progress on the same step, moving away costs it."""
    env, fake = make_env(fake_kwargs={"max_steps_per_zone": 10_000})
    env.reset()
    weight = base_config["progress_weight"]
    right, left = P.ACTION_RIGHT, P.ACTION_LEFT
    # All zones have target > spawn, so RIGHT is progress.
    _, r, d, _ = env.step(actions_for([left] * 3))
    assert np.allclose(r, -weight) and not d.any()
    _, r, d, _ = env.step(actions_for([right] * 3))
    assert np.allclose(r, weight)


def test_win_returns_new_episode_obs_and_terminal_obs(make_env, base_config):
    """B8: the obs returned with done=True is the new episode's first state."""
    env, fake = make_env(num_bots=1, fake_kwargs={"max_steps_per_zone": 10_000})
    obs = env.reset()
    spawn = obs["stats"][0, 2]
    zone = env.current_zone[0]
    dist = zone.original_location.target_x - zone.original_location.spawn_x
    for t in range(1, int(dist) + 1):
        obs, r, d, infos = env.step(actions_for([P.ACTION_RIGHT]))
    assert d[0]
    info = infos[0]
    assert info["outcome"] == "win" and info["is_success"] is True
    assert info["TimeLimit.truncated"] is False
    assert r[0] == base_config["win_reward"]
    # Terminal obs: bot on the target in the old zone; returned obs: at the spawn of the next zone
    term = info["terminal_observation"]
    assert term["stats"][3] == zone.original_location.target_x
    assert term["stats"][2] == spawn
    new_zone = env.current_zone[0]
    assert obs["stats"][0, 3] == new_zone.original_location.spawn_x
    assert obs["stats"][0, 2] == new_zone.original_location.spawn_x
    assert term["vision"][0, 0, 0] == obs["vision"][0, 0, 0, 0]  # same packet


def test_timeout_is_truncation(make_env, base_config):
    """B7: timeouts set TimeLimit.truncated so SB3 bootstraps from terminal_observation."""
    env, fake = make_env(num_bots=2, fake_kwargs={"max_steps_per_zone": 5})
    env.reset()
    for _ in range(4):
        _, _, d, _ = env.step(actions_for([0, 0]))
        assert not d.any()
    _, r, d, infos = env.step(actions_for([0, 0]))
    assert d.all()
    for info in infos:
        assert info["outcome"] == "timeout"
        assert info["TimeLimit.truncated"] is True
        assert info["is_success"] is False
        assert "terminal_observation" in info
    assert np.allclose(r, base_config["timeout_penalty"])


def test_death_is_terminal_not_truncated(make_env, base_config):
    die_at = lambda bot, k, sim: P.FLAG_DONE | P.FLAG_DIE if (bot == 1 and k == 3) else 0
    env, fake = make_env(num_bots=2, fake_kwargs={"forced_flags": die_at, "max_steps_per_zone": 10_000})
    env.reset()
    for t in range(1, 4):
        _, r, d, infos = env.step(actions_for([0, 0]))
    assert list(d) == [False, True]
    assert infos[1]["outcome"] == "died" and infos[1]["TimeLimit.truncated"] is False
    assert r[1] == base_config["death_penalty"]


def test_aborted_episode_does_not_count_in_zone_stats(make_env):
    """B11: an aborted episode ends exactly once and is not recorded as a zone attempt."""
    abort = lambda bot, k, sim: P.FLAG_DONE | P.FLAG_ABORTED if (bot == 0 and k == 2) else 0
    env, fake = make_env(num_bots=1, fake_kwargs={"forced_flags": abort, "max_steps_per_zone": 10_000})
    env.reset()
    dones = []
    for _ in range(6):
        _, r, d, infos = env.step(actions_for([0]))
        dones.append(bool(d[0]))
        if d[0]:
            assert infos[0]["outcome"] == "aborted" and infos[0]["TimeLimit.truncated"] is True
            assert r[0] == 0.0
    assert dones.count(True) == 1
    assert sum(z.stats.times_trained for z in env.zone_manager.zones) == 0


def test_next_zone_is_queued_in_advance(make_env):
    """Python always has a zone queued in C#, so C# can switch zones in the tick the episode ends."""
    env, fake = make_env(num_bots=2, fake_kwargs={"max_steps_per_zone": 3})
    env.reset()
    for _ in range(9):
        env.step(actions_for([0, 0]))
    # handshake zone started at packet 0, then a new zone after every timeout (packets 3, 6, 9)
    starts = [(k, i) for k, i, _ in fake.zone_starts]
    assert starts == [(0, 0), (0, 1), (3, 0), (3, 1), (6, 0), (6, 1), (9, 0), (9, 1)]
    # every started zone (after the first) was sent by Python before the episode ended
    sent = {(i, z) for _, i, z in fake.zone_commands}
    for k, i, zone in fake.zone_starts[2:]:
        assert (i, zone) in sent
    # Python's view of the current zone matches the fake game's
    for i in range(2):
        loc = env.current_zone[i].original_location
        assert fake.zone[i][:3] == (loc.spawn_x, loc.spawn_y, loc.target_x)


def test_disconnect_raises_instead_of_fabricating_data(make_env):
    """B6: a lost connection stops training; no zero-filled transitions are returned."""
    env, fake = make_env(fake_kwargs={"disconnect_after": 3})
    env.reset()
    env.step(actions_for([0, 0, 0]))
    env.step(actions_for([0, 0, 0]))
    with pytest.raises(P.TerrariaConnectionError):
        env.step(actions_for([0, 0, 0]))
        env.step(actions_for([0, 0, 0]))


def test_port_can_be_reused_right_after_close(make_env):
    """B5: SO_REUSEADDR lets the dashboard restart training on the same port."""
    env, fake = make_env()
    env.reset()
    env.step(actions_for([0, 0, 0]))
    port = env.port
    env.close()
    fake.join(timeout=5)
    s = socket.socket()
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("127.0.0.1", port))
    s.close()


def test_single_connection_only(make_env):
    """B5: the env accepts exactly one connection (no second blocking accept)."""
    env, fake = make_env()
    obs = env.reset()  # would hang if __init__ waited for a second connection
    assert obs["stats"].shape == (3, P.STATS_SIZE)


def test_packet_round_trip():
    rng = np.random.default_rng(1)
    n = 3
    stats = rng.normal(size=(n, P.STATS_SIZE)).astype(np.float32)
    vision = rng.integers(0, 3, size=(n, 1, P.GRID_SIDE, P.GRID_SIDE)).astype(np.float32) / 2
    dists = [1.5, -1.0, 30.0]
    flags = [0, P.FLAG_DONE | P.FLAG_WIN, P.FLAG_ZONE_SWITCHED]
    data = P.encode_packet(stats, vision, dists, flags, [stats[1]], [vision[1, 0]])
    assert len(data) == P.head_size(n) + P.OBS_BYTES
    s, v, d, f = P.parse_head(data[:P.head_size(n)], n)
    np.testing.assert_array_equal(s, stats)
    np.testing.assert_array_equal(v, vision)
    np.testing.assert_allclose(d, dists)
    assert list(f) == flags
    ts, tv = P.parse_terminal(data[P.head_size(n):], 1)
    np.testing.assert_array_equal(ts[0], stats[1])
    np.testing.assert_array_equal(tv[0], vision[1])


def test_action_encoding():
    data = P.encode_actions(np.array([[1, 0, 1], [0, 1, 0]]), {1: (1.0, 2.0, 3.0, 4.0)})
    assert data[:4] == (P.ACTION_LEFT | P.ACTION_JUMP).to_bytes(4, "little")
    cmd = int.from_bytes(data[4:8], "little")
    assert cmd == P.ZONE_COMMAND_OFFSET + P.ACTION_RIGHT
    assert np.frombuffer(data[8:], dtype="<f4").tolist() == [1.0, 2.0, 3.0, 4.0]
