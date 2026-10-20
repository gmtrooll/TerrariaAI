"""Navigate environment over several headless tModLoader servers (mod side: Common/Env/NavigateEnv.cs, protocol
"TNV1"). One server per world, N bots per server; every bot is one environment of a vectorized env.

Implements Stable-Baselines3's VecEnv interface so SB3 PPO and the custom PPO train on exactly the same thing.
Observations are a dict of numpy arrays: fine (4, H, W) uint8, coarse (4, h, w) uint8 when the grid has an
overview, vec (F,) float32. Actions: MultiDiscrete [3, 3, 2] = horizontal (left, none, right), vertical (up, none,
down), jump.
"""

import json
import math
import random
import socket
import struct
import sys
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path

import gymnasium as gym
import numpy as np
from stable_baselines3.common.vec_env.base_vec_env import VecEnv

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from terraria_mcp import config as tml_config  # noqa: E402
from terraria_mcp import scenarios as scenario_files  # noqa: E402
from terraria_mcp.game import GameManager  # noqa: E402

MAGIC = 0x31564E54
# Per-bot command: horizontal, vertical, jump, has_next (u8 each), next scenario (5 x i32 in protocol version 1,
# 6 x i32 from version 2: + loadout), packed = 24 / 28 bytes.
def command_dtype(scenario_ints: int) -> np.dtype:
    return np.dtype([("a", np.uint8, (4,)), ("s", "<i4", (scenario_ints,))])

# Equipment variants: name -> (scenario set file suffix, loadout code sent to the server).
LOADOUTS = {"tsunami": ("", 1), "none": ("_j6", 0)}
DONE, WIN, DIED, TIMEOUT, NEW_EPISODE = 1, 2, 4, 8, 16
BANDS = ["5-15", "15-30", "30-60", "60-120"]


@dataclass
class RewardConfig:
    win: float = 20.0
    death: float = -5.0
    timeout: float = -1.0
    progress_per_tile: float = 0.5
    max_progress_per_step: float = 3.0
    damage: float = 5.0  # per full life lost (life fraction); 0 = off. Not on the decision a bot dies (death covers it)


def max_decisions(path_distance: int, frame_skip: int = 4, physics_ticks: int = None) -> int:
    """Episode time limit: generous for the distance (a bot covers ~0.75 tiles per 4-tick decision at run speed).
    Scaled with the decision interval, so every interval gets the same game time. With a physics route time
    (annotate_physics), twice that time plus 40 decisions."""
    if physics_ticks is not None and physics_ticks >= 0:
        return 40 * 4 // frame_skip + 2 * int(physics_ticks) // frame_skip
    return (40 + 4 * int(path_distance)) * 4 // frame_skip


class Curriculum:
    """Distance bands unlock in order: the next band opens when the success rate over the last 200 episodes of
    the hardest open band reaches `unlock_at`. Half the episodes come from the hardest open band, half uniformly
    from all open bands (so easier bands are not forgotten)."""

    def __init__(self, unlock_at: float = 0.5, window: int = 200, start_band: int = 0, fixed: bool = False):
        self.unlock_at = unlock_at
        self.window = window
        self.open = start_band + 1 if not fixed else len(BANDS)
        self.history = {b: [] for b in BANDS}
        self.fixed = fixed

    def pick_band(self, rng: random.Random) -> str:
        if rng.random() < 0.5:
            return BANDS[self.open - 1]
        return BANDS[rng.randrange(self.open)]

    def record(self, band: str, win: bool):
        h = self.history[band]
        h.append(1.0 if win else 0.0)
        if len(h) > self.window:
            del h[0]
        hardest = BANDS[self.open - 1]
        hh = self.history[hardest]
        if not self.fixed and self.open < len(BANDS) and len(hh) >= self.window and sum(hh) / len(hh) >= self.unlock_at:
            self.open += 1

    def success(self) -> dict:
        return {b: (sum(h) / len(h) if h else None) for b, h in self.history.items()}


DETOUR_RATIO = 1.7


def detour_ratio(s: dict) -> float:
    """Route length over straight-line distance, start to target."""
    return s["path_distance"] / max(1.0, math.hypot(s["target_x"] - s["start_x"], s["target_y"] - s["start_y"]))


class ScenarioSource:
    """Per world: scenarios grouped by band. Training samples through the curriculum; evaluation walks a fixed
    list in order.

    replay > 0 (prioritized replay of hard scenarios, after Prioritized Level Replay): that share of the episodes
    picks, within the chosen band, a scenario with probability proportional to its estimated failure rate
    (wins + 1) / (attempts + 2) from `stats`; the rest stay uniform, so new scenarios are still seen.

    detour > 0: that share of the episodes picks, within the chosen band, a detour scenario (route at least
    DETOUR_RATIO times the straight-line distance: the bot has to move away from the target first). They are
    about 15% of the scenarios and the policy's weakest group (Oct 20)."""

    def __init__(self, world: str, curriculum: Curriculum, rng: random.Random, fixed_list: list = None,
                 replay: float = 0.0, stats: dict = None, loadouts: list = None, physics: bool = False, detour: float = 0.0):
        self.world = world
        self.detour = detour
        self.curriculum = curriculum
        self.rng = rng
        self.replay = replay
        self.stats = stats if stats is not None else {}
        # One scenario set per equipment variant; an episode first picks a variant uniformly, then a band.
        self.sets = []
        for name in loadouts or ["tsunami"]:
            suffix, code = LOADOUTS[name]
            rows = scenario_files.load(world, suffix) if fixed_list is None else []
            for i, s in enumerate(rows):
                s["id"] = f"{world}{suffix}:{i}"
                s["loadout"] = code
            # Scenarios the movement graph cannot reach are kept: the graph still misses some moves (95.9% of the
            # scenarios bots solve are reachable in it, train_01, Oct 19), and the mod falls back to the grid distance
            # for an episode whose start is off the graph.
            self.sets.append({b: [s for s in rows if s["band"] == b] for b in BANDS})
            self.sets[-1]["detours"] = {b: [s for s in rows if s["band"] == b and detour_ratio(s) >= DETOUR_RATIO] for b in BANDS}
        self.by_band = self.sets[0]
        self.fixed = list(fixed_list) if fixed_list is not None else None
        self.fixed_pos = 0

    def next(self) -> dict:
        if self.fixed is not None:
            s = self.fixed[self.fixed_pos % len(self.fixed)]
            self.fixed_pos += 1
            return s
        by_band = self.sets[self.rng.randrange(len(self.sets))] if len(self.sets) > 1 else self.by_band
        band = self.curriculum.pick_band(self.rng)
        pool = by_band[band]
        if self.detour > 0 and by_band["detours"][band] and self.rng.random() < self.detour:
            return self.rng.choice(by_band["detours"][band])
        if self.replay > 0 and self.rng.random() < self.replay:
            weights = []
            for s in pool:
                attempts, wins = self.stats.get(s["id"], (0, 0))
                weights.append(1.0 - (wins + 1) / (attempts + 2))
            return self.rng.choices(pool, weights=weights)[0]
        return self.rng.choice(pool)


@dataclass
class Server:
    world: str
    sock: socket.socket
    bots: int
    obs_bytes: int
    spec: dict
    source: ScenarioSource
    current: list = field(default_factory=list)   # scenario per bot
    queued: list = field(default_factory=list)    # next scenario per bot (already sent)
    need_next: list = field(default_factory=list)  # bot consumed its queued scenario; send a new one
    buffer: bytearray = None
    queued_ints: np.ndarray = None                 # (bots, 5 or 6) int32: the queued scenarios as sent
    version: int = 1                               # protocol version the server speaks


def _recv_exact(sock: socket.socket, view: memoryview, n: int):
    got = 0
    while got < n:
        k = sock.recv_into(view[got:n], n - got)
        if k == 0:
            raise ConnectionError("server closed the environment connection")
        got += k


class NavigateVecEnv(VecEnv):
    def __init__(self, worlds: list, bots_per_server: int = 32, port: int = 5600, obs: str = "grid=fovea,visibility=exposed",
                 frame_skip: int = 4, fidelity: str = "world_updates=0,npc_spawns=0", bot_lean: bool = True,
                 reward: RewardConfig = None, curriculum: Curriculum = None, seed: int = 0, fixed_scenarios: dict = None,
                 games: GameManager = None, launch_timeout: float = 300, mode: str = "server", extra_args: list = None,
                 replay: float = 0.0, loadouts: list = None, graph_dir: str = None, detour: float = 0.0):
        """graph_dir: physics movement graphs (navigate.movegraph); the progress reward then uses physics route
        distances (option C), only physically reachable scenarios are used, and time limits follow physics times."""
        """mode "client" launches game windows instead of headless servers (for watching; see navigate.watch)."""
        self.frame_skip = frame_skip
        self.replay = replay
        self.detour = detour
        self.loadouts = loadouts or ["tsunami"]
        self.physics = graph_dir is not None
        self.reward_cfg = reward or RewardConfig()
        self.curriculum = curriculum or Curriculum()
        self.rng = random.Random(seed)
        self.games = games or GameManager()
        self.instance_names = []
        self.episode_log = []      # finished episodes since the last pop
        self.scenario_stats = {}   # scenario id -> [attempts, wins]

        listener = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind(("127.0.0.1", port))
        listener.listen(len(worlds))
        listener.settimeout(launch_timeout)

        args = ["-env", str(port), "-frameskip", str(frame_skip), "-fidelity", fidelity, "-obs", obs]
        if bot_lean:
            args.append("-botlean")
        args += list(extra_args or [])
        if graph_dir:
            args += ["-envgraph", str(graph_dir)]
        errors = []

        def launch(i, world):
            # wait=False: a server that has connected to this learner blocks inside its tick waiting for actions, so
            # it cannot answer the bridge's readiness check; readiness is the connection itself (accepted below).
            name = f"{'watch' if mode == 'client' else 'env'}{port}_{i}_{world}"
            try:
                self.games.launch(mode, world=world, bots=bots_per_server, name=name, extra_args=args, wait=False)
                self.instance_names.append(name)
            except Exception as e:  # noqa: BLE001
                errors.append(f"{world}: {e}")

        threads = [threading.Thread(target=launch, args=(i, w)) for i, w in enumerate(worlds)]
        for t in threads:
            t.start()
            time.sleep(1.0)

        self.servers = []
        try:
            for _ in worlds:
                conn, _ = listener.accept()
                conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
                conn.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1 << 22)
                conn.settimeout(None)
                self.servers.append(self._handshake(conn, fixed_scenarios))
        finally:
            listener.close()
            for t in threads:
                t.join()
        if errors:
            raise RuntimeError("launch failed: " + " | ".join(errors))
        self.servers.sort(key=lambda s: worlds.index(s.world))

        spec = self.servers[0].spec["observation"]
        self.spec = spec
        layout = spec["layout"]
        self.fine_shape = (4, layout["fine"]["h"], layout["fine"]["w"])
        self.coarse_shape = (4, layout["coarse"]["h"], layout["coarse"]["w"]) if layout["coarse"] else None
        self.float_count = layout["floats"]["count"]
        self.obs_bytes = layout["total_bytes"]
        spaces = {"fine": gym.spaces.Box(0, 255, self.fine_shape, np.uint8),
                  "vec": gym.spaces.Box(-10, 10, (self.float_count,), np.float32)}
        if self.coarse_shape:
            spaces["coarse"] = gym.spaces.Box(0, 255, self.coarse_shape, np.uint8)
        n_envs = sum(s.bots for s in self.servers)
        super().__init__(n_envs, gym.spaces.Dict(spaces), gym.spaces.MultiDiscrete([3, 3, 2]))

        self._obs = {k: np.zeros((n_envs,) + sp.shape, sp.dtype) for k, sp in spaces.items()}
        self._dist = np.zeros(n_envs, np.float32)
        names = spec["self_names"] + spec["capability_names"] + spec["goal_names"]
        self._life_ix = names.index("life")
        self._ep_return = np.zeros(n_envs, np.float32)
        self._ep_len = np.zeros(n_envs, np.int32)
        self._actions = None
        # The first step packet of every server (after the handshake) holds the initial observations.
        offset = 0
        for s in self.servers:
            _, dist, _ = self._read_step(s, offset, initial=True)
            self._dist[offset:offset + s.bots] = dist
            offset += s.bots
        self._life = self._obs["vec"][:, self._life_ix].copy()

    # ---------- protocol ----------

    def _handshake(self, conn: socket.socket, fixed_scenarios: dict) -> Server:
        head = bytearray(24)
        _recv_exact(conn, memoryview(head), 24)
        magic, version, bots, frame_skip, obs_bytes, spec_len = struct.unpack("<Iiiiii", head)
        if magic != MAGIC:
            raise ConnectionError(f"bad magic {magic:#x}")
        spec_buf = bytearray(spec_len)
        _recv_exact(conn, memoryview(spec_buf), spec_len)
        spec = json.loads(spec_buf.decode("utf-8"))
        world = spec["world"]
        fixed = fixed_scenarios.get(world) if fixed_scenarios else None
        source = ScenarioSource(world, self.curriculum, self.rng, fixed, replay=self.replay, stats=self.scenario_stats,
                                loadouts=self.loadouts, physics=self.physics, detour=self.detour)
        server = Server(world, conn, bots, obs_bytes, spec, source)
        server.version = version
        if version < 2 and any(LOADOUTS[n][1] != 1 for n in self.loadouts):
            raise ConnectionError(f"server speaks protocol {version}, which has no loadouts; rebuild the mod")
        server.buffer = bytearray(4 + bots * (5 + obs_bytes) * 2 + bots * 4)
        server.queued_ints = np.zeros((bots, 6 if version >= 2 else 5), np.int32)
        out = bytearray()
        for b in range(bots):
            cur, nxt = source.next(), source.next()
            server.current.append(cur)
            server.queued.append(nxt)
            server.need_next.append(False)
            server.queued_ints[b] = self._scenario_ints(nxt, version)
            out += self._pack_scenario(cur, version) + self._pack_scenario(nxt, version)
        conn.sendall(out)
        return server

    def _scenario_ints(self, s: dict, version: int = 2) -> tuple:
        ints = (s["start_x"], s["start_y"], s["target_x"], s["target_y"],
                max_decisions(s["path_distance"], self.frame_skip, s.get("physics_ticks") if self.physics else None))
        return ints + (s.get("loadout", 1),) if version >= 2 else ints

    def _pack_scenario(self, s: dict, version: int = 2) -> bytes:
        ints = self._scenario_ints(s, version)
        return struct.pack(f"<{len(ints)}i", *ints)

    def _decode_block(self, rows: np.ndarray, start: int, target: dict):
        """rows: (n, obs_bytes) uint8, one observation per row -> target[...][start:start + n] (whole arrays at once)."""
        n = rows.shape[0]
        fine_n = self.fine_shape[0] * self.fine_shape[1] * self.fine_shape[2]
        target["fine"][start:start + n] = rows[:, :fine_n].reshape(n, self.fine_shape[1], self.fine_shape[2], 4).transpose(0, 3, 1, 2)
        o = fine_n
        if self.coarse_shape:
            coarse_n = self.coarse_shape[0] * self.coarse_shape[1] * self.coarse_shape[2]
            target["coarse"][start:start + n] = rows[:, o:o + coarse_n].reshape(n, self.coarse_shape[1], self.coarse_shape[2], 4).transpose(0, 3, 1, 2)
            o += coarse_n
        target["vec"][start:start + n] = np.ascontiguousarray(rows[:, o:o + 4 * self.float_count]).view(np.float32)

    def _decode_obs(self, raw: memoryview, i: int, target: dict):
        fine_n = self.fine_shape[0] * self.fine_shape[1] * self.fine_shape[2]
        fine = np.frombuffer(raw, np.uint8, fine_n, 0).reshape(self.fine_shape[1], self.fine_shape[2], 4)
        target["fine"][i] = fine.transpose(2, 0, 1)
        o = fine_n
        if self.coarse_shape:
            coarse_n = self.coarse_shape[0] * self.coarse_shape[1] * self.coarse_shape[2]
            coarse = np.frombuffer(raw, np.uint8, coarse_n, o).reshape(self.coarse_shape[1], self.coarse_shape[2], 4)
            target["coarse"][i] = coarse.transpose(2, 0, 1)
            o += coarse_n
        target["vec"][i] = np.frombuffer(raw, np.float32, self.float_count, o)

    def _single_obs(self, raw: memoryview) -> dict:
        one = {k: np.zeros((1,) + v.shape[1:], v.dtype) for k, v in self._obs.items()}
        self._decode_obs(raw, 0, one)
        return {k: v[0] for k, v in one.items()}

    def _read_step(self, s: Server, offset: int, initial: bool = False):
        """Read one step packet; returns per-bot (flags, terminal_distance, terminal_obs) for ended episodes."""
        n, ob = s.bots, s.obs_bytes
        fixed = 4 + n * (5 + ob)
        view = memoryview(s.buffer)
        _recv_exact(s.sock, view, fixed)
        rows = np.frombuffer(s.buffer, np.uint8, n * (5 + ob), 4).reshape(n, 5 + ob)
        flags = rows[:, 0].copy()
        dist = np.ascontiguousarray(rows[:, 1:5]).view(np.float32).reshape(n).copy()
        self._decode_block(rows[:, 5:], offset, self._obs)
        ended = np.flatnonzero(flags & DONE).tolist()
        terminal = {}
        if ended:
            extra = len(ended) * (4 + ob)
            tail = bytearray(extra)
            _recv_exact(s.sock, memoryview(tail), extra)
            trows = np.frombuffer(tail, np.uint8).reshape(len(ended), 4 + ob)
            tdist = np.ascontiguousarray(trows[:, :4]).view(np.float32).reshape(-1)
            tobs = {k: np.zeros((len(ended),) + v.shape[1:], v.dtype) for k, v in self._obs.items()}
            self._decode_block(trows[:, 4:], 0, tobs)
            for k, b in enumerate(ended):
                terminal[b] = (float(tdist[k]), {key: v[k] for key, v in tobs.items()})
        return flags, dist, terminal

    # ---------- VecEnv ----------

    def reset(self):
        """Episodes reset themselves on the server; this returns the current observations (the first ones after
        connecting, when called before any step)."""
        return {k: v.copy() for k, v in self._obs.items()}

    def step_async(self, actions):
        self._actions = np.asarray(actions, dtype=np.int64).reshape(self.num_envs, 3)
        offset = 0
        for s in self.servers:
            out = np.zeros(s.bots, command_dtype(s.queued_ints.shape[1]))
            out["a"][:, :3] = self._actions[offset:offset + s.bots]
            for b in [b for b in range(s.bots) if s.need_next[b]]:  # only bots whose episode just ended
                nxt = s.source.next()
                s.queued[b] = nxt
                s.queued_ints[b] = self._scenario_ints(nxt, s.version)
                s.need_next[b] = False
                out["a"][b, 3] = 1
            out["s"] = s.queued_ints
            s.sock.sendall(out.tobytes())
            offset += s.bots

    def step_wait(self):
        n = self.num_envs
        rewards = np.zeros(n, np.float32)
        dones = np.zeros(n, bool)
        infos = [{} for _ in range(n)]
        prev = self._dist.copy()
        prev_life = self._life.copy()
        offset = 0
        cfg = self.reward_cfg
        for s in self.servers:
            flags, dist, terminal = self._read_step(s, offset)
            for b in range(s.bots):
                i = offset + b
                f = flags[b]
                if f & DONE:
                    tdist, tobs = terminal[b]
                    r = float(np.clip(prev[i] - tdist, -cfg.max_progress_per_step, cfg.max_progress_per_step)) * cfg.progress_per_tile
                    win = bool(f & WIN)
                    if win:
                        r += cfg.win
                    elif f & DIED:
                        r += cfg.death
                    elif f & TIMEOUT:
                        r += cfg.timeout
                    if not f & DIED:
                        r -= cfg.damage * max(0.0, float(prev_life[i] - tobs["vec"][self._life_ix]))
                    rewards[i] = r
                    dones[i] = True
                    infos[i]["terminal_observation"] = tobs
                    infos[i]["TimeLimit.truncated"] = bool(f & TIMEOUT)
                    sc = s.current[b]
                    self._ep_return[i] += r
                    self._ep_len[i] += 1
                    episode = {"r": float(self._ep_return[i]), "l": int(self._ep_len[i]), "win": win,
                               "died": bool(f & DIED), "timeout": bool(f & TIMEOUT), "band": sc["band"],
                               "layer": sc["layer"], "world": s.world, "id": sc["id"], "path_distance": sc["path_distance"],
                               "loadout": sc.get("loadout", 1)}
                    infos[i]["episode"] = episode
                    self.episode_log.append(episode)
                    st = self.scenario_stats.setdefault(sc["id"], [0, 0])
                    st[0] += 1
                    st[1] += int(win)
                    if s.source.fixed is None:
                        self.curriculum.record(sc["band"], win)
                    # The server moved the bot to its queued scenario; queue a new one with the next actions.
                    s.current[b] = s.queued[b]
                    s.need_next[b] = True
                    self._ep_return[i] = 0
                    self._ep_len[i] = 0
                else:
                    r = float(np.clip(prev[i] - dist[b], -cfg.max_progress_per_step, cfg.max_progress_per_step)) * cfg.progress_per_tile
                    r -= cfg.damage * max(0.0, float(prev_life[i] - self._obs["vec"][i, self._life_ix]))
                    rewards[i] = r
                    self._ep_return[i] += r
                    self._ep_len[i] += 1
                self._dist[i] = dist[b]
                self._life[i] = self._obs["vec"][i, self._life_ix]  # after a reset: the new episode's first life
            offset += s.bots
        return {k: v.copy() for k, v in self._obs.items()}, rewards, dones, infos

    def pop_episodes(self) -> list:
        episodes, self.episode_log = self.episode_log, []
        return episodes

    def close(self):
        for s in self.servers:
            try:
                s.sock.close()
            except OSError:
                pass
        for name in self.instance_names:
            try:
                self.games.stop(name, timeout=10)
            except Exception:  # noqa: BLE001
                pass

    # SB3 plumbing that this env does not need.
    def get_attr(self, attr_name, indices=None):
        return [getattr(self, attr_name, None)] * self.num_envs

    def set_attr(self, attr_name, value, indices=None):
        pass

    def env_method(self, method_name, *method_args, indices=None, **method_kwargs):
        return [None] * self.num_envs

    def env_is_wrapped(self, wrapper_class, indices=None):
        return [False] * self.num_envs

    def seed(self, seed=None):
        return [None] * self.num_envs
