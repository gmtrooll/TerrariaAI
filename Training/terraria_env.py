"""
Stable-Baselines3 VecEnv backed by the tModLoader mod over a local TCP socket.

Python is the server; the mod connects (and reconnects) to it. See protocol.py for the bytes.

Timing: the mod sends packet k, then blocks reading actions, then applies them for frame_skip
ticks and sends packet k+1. reset() reads packet 0 and step(a_k) returns packet k+1, so every
returned observation / reward describes the state after the action that was just sent.

Episodes: when a bot's episode ends (win, death, timeout, aborted) the mod moves it into its
queued zone in the same tick, so the observation returned with done=True already belongs to the
new episode and the old episode's last observation arrives in infos["terminal_observation"].
Python keeps one zone queued per bot and refills the queue whenever the mod reports that it
used it (FLAG_ZONE_SWITCHED).
"""
import socket
import warnings
from typing import Any, Dict, List, Optional

import numpy as np
from gymnasium import spaces
from stable_baselines3.common.vec_env import VecEnv

import protocol as P
from config_loader import load_training_config
from reward_system import (OUTCOME_ABORTED, OUTCOME_DIED, OUTCOME_WIN, TRUNCATED_OUTCOMES,
                           RewardConfig, RewardSystem)
from zone_manager import TrainingZone, ZoneManager, get_default_database_path

ZONE_STATS_SAVE_EVERY = 500  # finished zone episodes between zone stats reports


class TerrariaVecEnv(VecEnv):
    def __init__(self, num_envs: Optional[int] = None, zone_database_path: Optional[str] = None,
                 config: Optional[Dict[str, Any]] = None, port: Optional[int] = None,
                 zone_stats_path: Optional[str] = None, accept_timeout: Optional[float] = None):
        cfg = dict(config) if config is not None else load_training_config()
        self.cfg = cfg
        self.NUM_ENVS = int(num_envs if num_envs is not None else cfg.get('num_bots', 16))
        self.debug = bool(cfg.get('debug', False))
        self.GRID_SIZE = P.GRID_SIDE
        self.STATS_SIZE = P.STATS_SIZE

        # --- ZONE MANAGEMENT ---
        db_path = zone_database_path or get_default_database_path(cfg.get('zone_database_path'))
        self.zone_manager = ZoneManager(db_path, seed=cfg.get('seed'), verbose=self.debug)
        self.zone_manager.min_difficulty = cfg.get('zone_min_difficulty', 1)
        self.zone_manager.max_difficulty = cfg.get('zone_max_difficulty', 10)
        self.zone_manager.use_performance_sampling = cfg.get('use_performance_sampling', True)
        self.zone_stats_path = zone_stats_path
        self._zone_episodes_since_save = 0

        self.current_zone: List[Optional[TrainingZone]] = [None] * self.NUM_ENVS
        self.queued_zone: List[Optional[TrainingZone]] = [None] * self.NUM_ENVS
        self.queued_unsent = [False] * self.NUM_ENVS

        self.reward_system = RewardSystem(RewardConfig.from_dict(cfg))

        observation_space = spaces.Dict({
            "vision": spaces.Box(low=0, high=1, shape=(1, self.GRID_SIZE, self.GRID_SIZE), dtype=np.float32),
            "stats":  spaces.Box(low=-np.inf, high=np.inf, shape=(self.STATS_SIZE,), dtype=np.float32)
        })
        super().__init__(self.NUM_ENVS, observation_space, spaces.MultiBinary(3))

        self.handshake: Optional[P.Handshake] = None
        self._started = False
        self._last_obs = None
        self._head_buffer = bytearray(P.head_size(self.NUM_ENVS))
        self.conn: Optional[socket.socket] = None

        # Networking: the mod connects to us
        self.port = int(port if port is not None else cfg.get('port', 5555))
        self.server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)  # quick restarts from the dashboard
        try:
            self.server.bind(('127.0.0.1', self.port))
            self.server.listen(1)
            self.server.settimeout(accept_timeout)
            self.wait_for_connection()
        except BaseException:
            self.close()
            raise

    @property
    def use_zones(self) -> bool:
        return len(self.zone_manager.zones) > 0

    # ------------------------------------------------------------------ connection

    def wait_for_connection(self):
        print(f"Waiting for Terraria ({self.NUM_ENVS} bots) on port {self.port}...")
        conn, addr = self.server.accept()
        conn.settimeout(None)
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        self.conn = conn
        print(f"Connected to {addr}")
        self._handshake()

    def _handshake(self):
        head = self._recv_exact(P.HEADER.size)
        fields = P.HEADER.unpack(head)
        if fields[0] != P.MAGIC:
            self._reject(P.validate_handshake(P.Handshake(*fields[:8], ""), self.NUM_ENVS))
        name_len = fields[8]
        if not 0 <= name_len <= 4096:
            self._reject([f"bad world name length {name_len}"])
        world_name = self._recv_exact(name_len).decode("utf-8", errors="replace")
        h = P.Handshake(*fields[:8], world_name)

        problems = P.validate_handshake(h, self.NUM_ENVS)
        if self.use_zones and world_name:
            dropped = self.zone_manager.filter_world(world_name)
            if dropped:
                print(f"[ZoneEnv] Ignoring {dropped} zones recorded in other worlds")
            if not self.use_zones:
                problems.append(f"none of the zones in {self.zone_manager.database_path} belong to world "
                                f"'{world_name}'; load the world the zones were recorded in")
        if problems:
            self._reject(problems)

        self.handshake = h
        if h.max_steps_per_zone != self.cfg.get('max_steps_per_zone', h.max_steps_per_zone) or \
                h.frame_skip != self.cfg.get('frame_skip', h.frame_skip):
            print(f"[ZoneEnv] Note: the mod uses frame_skip={h.frame_skip}, max_steps_per_zone={h.max_steps_per_zone} "
                  f"but this config says {self.cfg.get('frame_skip')}, {self.cfg.get('max_steps_per_zone')}; "
                  f"the mod's values are the ones in effect")

        # Initial zone for every bot, applied by the mod before its first packet.
        zones = []
        for i in range(self.NUM_ENVS):
            zone = self.zone_manager.sample_zone() if self.use_zones else None
            self.current_zone[i] = None
            self.queued_zone[i] = zone
            self.queued_unsent[i] = False
            zones.append(self._zone_tuple(zone) if zone else None)
        mode = f"{len(self.zone_manager.zones)} zones" if self.use_zones else "no zones (target marker mode)"
        self._sendall(P.encode_handshake_reply(P.HANDSHAKE_OK, f"Python trainer ready: {mode}", zones))
        print(f"[ZoneEnv] Handshake OK: world '{world_name}', {h.num_bots} bots, frame_skip {h.frame_skip}, {mode}")

    def _reject(self, problems: List[str]):
        message = "Configuration mismatch between mod and trainer: " + "; ".join(problems)
        try:
            self._sendall(P.encode_handshake_reply(P.HANDSHAKE_REJECTED, message))
        except P.TerrariaConnectionError:
            pass
        self._close_conn()
        raise P.HandshakeError(message)

    @staticmethod
    def _zone_tuple(zone: TrainingZone) -> P.Zone:
        loc = zone.original_location
        return (loc.spawn_x, loc.spawn_y, loc.target_x, loc.target_y)

    def _recv_exact(self, n: int) -> bytes:
        buf = bytearray(n)
        self._recv_into(buf)
        return bytes(buf)

    def _recv_into(self, buf):
        if self.conn is None:
            raise P.TerrariaConnectionError("not connected to Terraria")
        view = memoryview(buf)
        pos = 0
        try:
            while pos < len(buf):
                read = self.conn.recv_into(view[pos:])
                if read == 0:
                    raise ConnectionResetError("Terraria closed the connection")
                pos += read
        except OSError as e:
            self._close_conn()
            raise P.TerrariaConnectionError(f"lost connection to Terraria: {e}") from e

    def _sendall(self, data: bytes):
        if self.conn is None:
            raise P.TerrariaConnectionError("not connected to Terraria")
        try:
            self.conn.sendall(data)
        except OSError as e:
            self._close_conn()
            raise P.TerrariaConnectionError(f"lost connection to Terraria: {e}") from e

    def _close_conn(self):
        if self.conn is not None:
            try:
                self.conn.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            self.conn.close()
            self.conn = None

    # ------------------------------------------------------------------ VecEnv API

    def env_is_wrapped(self, wrapper_class, indices=None): return [False] * self.NUM_ENVS
    def env_method(self, method_name, *method_args, indices=None, **method_kwargs): return [None] * self.NUM_ENVS
    def get_attr(self, attr_name, indices=None): return [None] * self.NUM_ENVS
    def set_attr(self, attr_name, value, indices=None): pass

    def reset(self):
        """
        Returns the real first observation (packet 0). Episodes cannot be restarted on demand:
        a second reset() returns the latest observation and the running episodes continue.
        """
        if self._started:
            warnings.warn("TerrariaVecEnv.reset() called again: episodes run on in the game; returning the latest observation")
            return self._last_obs
        stats, vision, dists, flags, _, _ = self._read_packet()
        for i in range(self.NUM_ENVS):
            if flags[i] & P.FLAG_ZONE_SWITCHED:
                self._rotate_zone(i)
            self.reward_system.reset(i, float(dists[i]))
        self._started = True
        self._last_obs = {"vision": vision, "stats": stats}
        return self._last_obs

    def step_async(self, actions):
        zones_to_send = {}
        for i in range(self.NUM_ENVS):
            if self.queued_unsent[i] and self.queued_zone[i] is not None:
                zones_to_send[i] = self._zone_tuple(self.queued_zone[i])
                self.queued_unsent[i] = False
        self._sendall(P.encode_actions(actions, zones_to_send))

    def step_wait(self):
        stats, vision, dists, flags, term_stats, term_vision = self._read_packet()

        rewards = np.zeros(self.NUM_ENVS, dtype=np.float32)
        dones = np.zeros(self.NUM_ENVS, dtype=bool)
        infos: List[Dict[str, Any]] = [{} for _ in range(self.NUM_ENVS)]
        terminal_index = 0

        for i in range(self.NUM_ENVS):
            reward, done, info = self.reward_system.calculate_reward(i, float(dists[i]), int(flags[i]))
            rewards[i] = reward
            dones[i] = done
            if done:
                outcome = info["outcome"]
                infos[i]["outcome"] = outcome
                infos[i]["is_success"] = outcome == OUTCOME_WIN
                infos[i]["terminal_observation"] = {
                    "vision": term_vision[terminal_index],
                    "stats": term_stats[terminal_index],
                }
                terminal_index += 1
                # Timeouts / interruptions are not true terminal states: SB3 bootstraps from
                # terminal_observation when this is set.
                infos[i]["TimeLimit.truncated"] = outcome in TRUNCATED_OUTCOMES
                self._record_episode(i, outcome, reward)
            if flags[i] & P.FLAG_ZONE_SWITCHED:
                self._rotate_zone(i)

        self._last_obs = {"vision": vision, "stats": stats}
        return self._last_obs, rewards, dones, infos

    def _read_packet(self):
        self._recv_into(self._head_buffer)
        stats, vision, dists, flags = P.parse_head(self._head_buffer, self.NUM_ENVS)
        n_done = int(np.count_nonzero(flags & P.FLAG_DONE))
        if n_done:
            tail = self._recv_exact(n_done * P.OBS_BYTES)
            term_stats, term_vision = P.parse_terminal(tail, n_done)
        else:
            term_stats = np.empty((0, self.STATS_SIZE), dtype=np.float32)
            term_vision = np.empty((0, 1, self.GRID_SIZE, self.GRID_SIZE), dtype=np.float32)
        if not (np.all(np.isfinite(stats)) and np.all(np.isfinite(vision))):
            raise P.ProtocolError("non-finite values in observation: stream is probably out of sync")
        return stats, vision, dists, flags, term_stats, term_vision

    # ------------------------------------------------------------------ zones

    def _rotate_zone(self, i: int):
        """The mod started bot i's queued zone: it becomes current, and a new one is queued."""
        self.current_zone[i] = self.queued_zone[i]
        if self.current_zone[i] is not None:
            self.zone_manager.assign_zone_to_bot(i, self.current_zone[i])
        self.queued_zone[i] = self.zone_manager.sample_zone() if self.use_zones else None
        self.queued_unsent[i] = self.queued_zone[i] is not None

    def _record_episode(self, i: int, outcome: str, reward: float):
        zone = self.current_zone[i]
        if self.debug:
            print(f"[ZoneEnv] Bot {i} episode done ({outcome}, reward={reward:.1f}, "
                  f"zone={zone.zone_id if zone else None})")
        if zone is None or outcome == OUTCOME_ABORTED:
            return
        self.zone_manager.record_episode_result(i, outcome == OUTCOME_WIN, outcome == OUTCOME_DIED)
        self._zone_episodes_since_save += 1
        if self.zone_stats_path and self._zone_episodes_since_save >= ZONE_STATS_SAVE_EVERY:
            self._zone_episodes_since_save = 0
            self.zone_manager.save_stats(self.zone_stats_path)

    # ------------------------------------------------------------------ cleanup

    def close(self):
        """Close all socket connections gracefully"""
        if self.zone_stats_path and self.use_zones:
            self.zone_manager.save_stats(self.zone_stats_path)
        self._close_conn()
        server = getattr(self, "server", None)
        if server is not None:
            server.close()
            self.server = None
