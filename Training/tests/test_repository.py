"""Checks of the repository's code and data that need neither the game nor torch (run by GitHub Actions on every push).
The behaviour itself is checked in the running game: physics_check, benchmarks and held-out evaluations."""

import json
import py_compile
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
SCENARIOS = REPO / "Training" / "scenarios"
RESULTS = REPO / "results" / "training"


def jsonl(path: Path) -> list:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


CODE = sorted((REPO / "Training" / "navigate").glob("*.py")) + sorted(p for p in (REPO / "tools").rglob("*.py") if ".venv" not in p.parts)


@pytest.mark.parametrize("path", CODE, ids=lambda p: str(p.relative_to(REPO)))
def test_python_compiles(path):
    py_compile.compile(str(path), doraise=True)


def test_loadout_table():
    rows = json.loads((REPO / "Training" / "loadouts.json").read_text())
    codes = [r["code"] for r in rows]
    names = [r["name"] for r in rows]
    assert len(set(codes)) == len(codes) and len(set(names)) == len(names)
    assert {0, 1} <= set(codes), "codes 0 (nothing) and 1 (Tsunami) keep their meaning"
    for r in rows:
        assert r["split"] in ("train", "test")
        assert r["jump_tiles"] >= 6 and len(r["features"]) == 4
        assert abs(r["features"][1] - r["jump_tiles"]) < 1.01 or r["jump_tiles"] == 6, r["name"]


MAIN_SETS = sorted(p for p in SCENARIOS.glob("*.jsonl") if "_" not in p.stem[p.stem.index("_") + 1:])


@pytest.mark.parametrize("path", MAIN_SETS, ids=lambda p: p.stem)
def test_main_scenario_set(path):
    world = path.stem
    assert (REPO / "worlds" / f"{world}.wld").exists(), f"{world}: the scenario set needs its world file in worlds/"
    rows = jsonl(path)
    assert len(rows) == 1200
    for key in ("start_x", "start_y", "target_x", "target_y", "path_distance", "band", "layer"):
        assert all(key in r for r in rows), key
    reach = SCENARIOS / f"{world}_reach.json"
    if reach.exists():
        assert all(len(v) == len(rows) for v in json.loads(reach.read_text()).values()), "reach file out of date"


@pytest.mark.parametrize("path", sorted(SCENARIOS.glob("*_h*.jsonl")), ids=lambda p: p.stem)
def test_height_sets(path):
    jump = int(path.stem.rsplit("_h", 1)[1])
    rows = jsonl(path)
    assert rows and all(r["max_jump"] == jump for r in rows)


@pytest.mark.parametrize("run", sorted(p.parent.name for p in RESULTS.glob("*/model.pt")))
def test_shipped_models_have_configs(run):
    config = json.loads((RESULTS / run / "config.json").read_text())
    assert "obs" in config and "steps" in config
