import math

import pytest

import protocol as P
from reward_system import RewardConfig, RewardSystem, outcome_from_flags

CFG = RewardConfig(win_reward=20, death_penalty=-5, timeout_penalty=-1, progress_weight=0.5, max_progress_per_step=5)


@pytest.mark.parametrize("flags, outcome, reward", [
    (P.FLAG_DONE | P.FLAG_WIN, "win", 20.0),
    (P.FLAG_DONE | P.FLAG_DIE, "died", -5.0),
    (P.FLAG_DONE | P.FLAG_TIMEOUT, "timeout", -1.0),
    (P.FLAG_DONE | P.FLAG_ABORTED, "aborted", 0.0),
    (P.FLAG_DONE, "aborted", 0.0),  # bare done
    (P.FLAG_DONE | P.FLAG_WIN | P.FLAG_ZONE_SWITCHED, "win", 20.0),
])
def test_terminal_rewards(flags, outcome, reward):
    rs = RewardSystem(CFG)
    rs.reset(0, 10.0)
    r, done, info = rs.calculate_reward(0, 30.0, flags)
    assert done and info["outcome"] == outcome and r == reward
    # the distance sent with done belongs to the next episode and seeds its progress tracking
    assert rs.prev_distances[0] == 30.0


def test_win_flag_without_done_is_not_terminal():
    assert outcome_from_flags(P.FLAG_WIN) is None


def test_progress_shaping():
    rs = RewardSystem(CFG)
    rs.reset(0, 10.0)
    assert rs.calculate_reward(0, 8.0, 0) == (1.0, False, {})   # 2 tiles closer
    assert rs.calculate_reward(0, 9.0, 0)[0] == -0.5            # 1 tile further
    assert rs.calculate_reward(0, 9.0, 0)[0] == 0.0             # standing still


def test_first_step_without_known_distance_gives_no_shaping():
    rs = RewardSystem(CFG)
    rs.reset(0)  # unknown
    assert rs.calculate_reward(0, 8.0, 0)[0] == 0.0
    assert rs.calculate_reward(0, 7.0, 0)[0] == 0.5


def test_progress_is_clipped():
    """Entering/leaving the BFS field changes distance by the off-path penalty: bounded reward."""
    rs = RewardSystem(CFG)
    rs.reset(0, 60.0)
    assert rs.calculate_reward(0, 10.0, 0)[0] == pytest.approx(2.5)
    assert rs.calculate_reward(0, 60.0, 0)[0] == pytest.approx(-2.5)


def test_oscillation_earns_nothing():
    rs = RewardSystem(CFG)
    rs.reset(0, 10.0)
    total = sum(rs.calculate_reward(0, d, 0)[0] for d in [9, 10, 9, 10, 9, 10])
    assert total == 0.0


@pytest.mark.parametrize("bad", [P.NO_DISTANCE, float("nan"), float("inf"), -5.0])
def test_missing_or_invalid_distance_never_leaks(bad):
    rs = RewardSystem(CFG)
    rs.reset(0, 10.0)
    r, done, _ = rs.calculate_reward(0, bad, 0)
    assert r == 0.0 and not done and math.isfinite(r)
    # shaping resumes from the last valid distance
    assert rs.calculate_reward(0, 9.0, 0)[0] == 0.5


def test_config_from_dict():
    cfg = RewardConfig.from_dict({"win_reward": 7, "unrelated": 1})
    assert cfg.win_reward == 7.0 and cfg.death_penalty == RewardConfig().death_penalty
