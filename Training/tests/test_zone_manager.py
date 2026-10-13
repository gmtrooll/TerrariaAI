import json
from collections import Counter

from fake_terraria import write_zone_db
from zone_manager import ZoneManager, get_default_database_path


def _manager(tmp_path, seed=0, **kw):
    db = write_zone_db(tmp_path / "zones.json", {
        "easy": (0, 0, 1, 0), "medium": (0, 0, 2, 0), "hard": (0, 0, 3, 0)})
    return ZoneManager(db, seed=seed, **kw)


def _set_rate(zone, successes, attempts):
    zone.stats.times_trained = attempts
    zone.stats.successful_attempts = successes
    zone.stats.failed_attempts = attempts - successes


def test_weights_follow_success_rate(tmp_path):
    zm = _manager(tmp_path)
    easy, medium, hard = zm.zones
    _set_rate(easy, 9, 10)    # 0.9 -> mastered
    _set_rate(medium, 5, 10)  # 0.5 -> learning
    _set_rate(hard, 1, 10)    # 0.1 -> struggling
    assert zm.zone_weight(easy) == ZoneManager.WEIGHT_MASTERED
    assert zm.zone_weight(medium) == ZoneManager.WEIGHT_LEARNING
    assert zm.zone_weight(hard) == ZoneManager.WEIGHT_STRUGGLING

    counts = Counter(zm.sample_zone().zone_id for _ in range(11000))
    total = ZoneManager.WEIGHT_MASTERED + ZoneManager.WEIGHT_LEARNING + ZoneManager.WEIGHT_STRUGGLING
    for zone_id, weight in [("easy", 0.5), ("medium", 2.0), ("hard", 3.0)]:
        assert abs(counts[zone_id] / 11000 - weight / total) < 0.02


def test_uniform_sampling_when_disabled(tmp_path):
    zm = _manager(tmp_path)
    zm.use_performance_sampling = False
    _set_rate(zm.zones[0], 10, 10)
    counts = Counter(zm.sample_zone().zone_id for _ in range(9000))
    assert all(abs(c / 9000 - 1 / 3) < 0.03 for c in counts.values())


def test_sampling_is_seeded(tmp_path):
    zm1, zm2 = _manager(tmp_path, seed=42), _manager(tmp_path, seed=42)
    assert [zm1.sample_zone().zone_id for _ in range(50)] == [zm2.sample_zone().zone_id for _ in range(50)]


def test_difficulty_filter(tmp_path):
    zm = _manager(tmp_path)
    zm.zones[0].difficulty = 1
    zm.set_difficulty_range(1, 2)
    assert {zm.sample_zone().zone_id for _ in range(50)} == {"easy"}


def test_record_and_save_stats_do_not_touch_database(tmp_path):
    zm = _manager(tmp_path)
    db_before = open(zm.database_path).read()
    zm.assign_zone_to_bot(0, zm.zones[0])
    zm.record_episode_result(0, success=True)
    zm.record_episode_result(0, success=False, died=True)
    out = tmp_path / "stats" / "zone_stats.json"
    zm.save_stats(str(out))
    report = {z["ZoneId"]: z for z in json.load(open(out))["Zones"]}
    assert report["easy"]["TimesTrained"] == 2 and report["easy"]["SuccessfulAttempts"] == 1
    assert open(zm.database_path).read() == db_before


def test_world_filter(tmp_path):
    zm = _manager(tmp_path)
    assert zm.filter_world("aTestingSP2") == 0
    assert zm.filter_world("Other") == 3 and zm.zones == []


def test_configured_database_path_wins(tmp_path):
    assert get_default_database_path(str(tmp_path / "z.json")) == str(tmp_path / "z.json")
