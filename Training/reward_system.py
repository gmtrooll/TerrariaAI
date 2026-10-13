"""
Reward for point-to-point navigation.

C# sends, per bot and decision, a world-anchored path distance to the bot's target (tiles of
walking/jumping, from a BFS field that respects jump height; see PathDistanceField.cs), or -1
when the bot has no target. The reward is

    terminal outcome reward (win / death / timeout)                       on the step the episode ends
    progress_weight * clip(prev_dist - dist, -max_progress, +max_progress)  on every other step

Shaping is a difference of a potential (-dist), so moving back and forth earns nothing over a
round trip; the clip bounds the jump when the bot enters or leaves the BFS field (where C#
switches to straight-line distance + off_path_penalty_tiles).
"""
import math
from dataclasses import dataclass
from typing import Dict, Optional, Tuple

from protocol import FLAG_DIE, FLAG_DONE, FLAG_TIMEOUT, FLAG_WIN, NO_DISTANCE

OUTCOME_WIN = "win"
OUTCOME_DIED = "died"
OUTCOME_TIMEOUT = "timeout"
OUTCOME_ABORTED = "aborted"
OUTCOMES = (OUTCOME_WIN, OUTCOME_DIED, OUTCOME_TIMEOUT, OUTCOME_ABORTED)
# Episodes cut short by time / interruption: the state was not terminal, so SB3 must bootstrap.
TRUNCATED_OUTCOMES = (OUTCOME_TIMEOUT, OUTCOME_ABORTED)


@dataclass
class RewardConfig:
    win_reward: float = 20.0
    death_penalty: float = -5.0
    timeout_penalty: float = -1.0
    progress_weight: float = 0.5
    max_progress_per_step: float = 5.0

    @classmethod
    def from_dict(cls, cfg: dict) -> "RewardConfig":
        defaults = cls()
        return cls(**{k: float(cfg.get(k, getattr(defaults, k))) for k in cls.__dataclass_fields__})


def outcome_from_flags(flags: int) -> Optional[str]:
    """Outcome of an episode that ended this step, or None if it did not end."""
    if not flags & FLAG_DONE:
        return None
    if flags & FLAG_WIN:
        return OUTCOME_WIN
    if flags & FLAG_DIE:
        return OUTCOME_DIED
    if flags & FLAG_TIMEOUT:
        return OUTCOME_TIMEOUT
    # FLAG_ABORTED, or a bare DONE from an older/unknown cause: treat as interrupted.
    return OUTCOME_ABORTED


class RewardSystem:
    def __init__(self, config: Optional[RewardConfig] = None):
        self.config = config or RewardConfig()
        self.prev_distances: Dict[int, Optional[float]] = {}

    def reset(self, bot_id: int, dist: float = NO_DISTANCE):
        """Start tracking a new episode; dist is the episode's first path distance (if known)."""
        self.prev_distances[bot_id] = self._valid(dist)

    @staticmethod
    def _valid(dist: float) -> Optional[float]:
        if dist is None or not math.isfinite(dist) or dist < 0:
            return None
        return float(dist)

    def calculate_reward(self, bot_id: int, dist: float, flags: int) -> Tuple[float, bool, dict]:
        """
        Returns (reward, done, info). When done, `dist` already belongs to the next episode
        (C# starts it in the same tick), so it seeds that episode's progress tracking.
        """
        cfg = self.config
        outcome = outcome_from_flags(flags)
        if outcome is not None:
            reward = {
                OUTCOME_WIN: cfg.win_reward,
                OUTCOME_DIED: cfg.death_penalty,
                OUTCOME_TIMEOUT: cfg.timeout_penalty,
                OUTCOME_ABORTED: 0.0,
            }[outcome]
            self.reset(bot_id, dist)
            return float(reward), True, {"outcome": outcome}

        reward = 0.0
        current = self._valid(dist)
        prev = self.prev_distances.get(bot_id)
        if current is not None:
            if prev is not None:
                progress = max(-cfg.max_progress_per_step, min(cfg.max_progress_per_step, prev - current))
                reward = cfg.progress_weight * progress
            self.prev_distances[bot_id] = current
        # No distance this step (no target, dead): keep prev so shaping resumes from it later.
        return float(reward), False, {}
