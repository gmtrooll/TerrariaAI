"""
A stand-in for the C# mod (RLNetworkSystem) that speaks the real protocol over a socket.

It mirrors the mod's per-decision order:
    apply last action (game ticks) -> end episodes + start queued zones -> send packet -> read actions
Bots live on a 1-D line: action bit RIGHT moves +1, LEFT moves -1. Observation fields used by
tests:
    stats[0] = action bits the bot was moved with since the previous packet
    stats[1] = index of this packet (0 for the one reset() reads)
    stats[2] = spawn_x of the bot's current zone (0 when it has none)
    stats[3] = bot position
    vision[0, 0, 0] = packet index (so terminal / current observations can be told apart)
"""
import socket
import struct
import threading
import time
from typing import Callable, Dict, List, Optional, Tuple

import numpy as np

import protocol as P

# (bot, packet_index, sim) -> extra flags to force this tick (e.g. FLAG_DONE | FLAG_DIE)
ForcedFlags = Callable[[int, int, "FakeTerraria"], int]


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class FakeTerraria(threading.Thread):
    def __init__(self, port: int, num_bots: int, *, world_name: str = "aTestingSP2",
                 max_steps_per_zone: int = 1000, frame_skip: int = 4, protocol_version: int = P.PROTOCOL_VERSION,
                 obs_version: int = P.OBS_VERSION, stats_size: int = P.STATS_SIZE, magic: int = P.MAGIC,
                 max_packets: Optional[int] = None, forced_flags: Optional[ForcedFlags] = None,
                 disconnect_after: Optional[int] = None):
        super().__init__(daemon=True)
        self.port = port
        self.num_bots = num_bots
        self.header = P.Handshake(magic, protocol_version, obs_version, num_bots, stats_size, P.GRID_SIDE,
                                  frame_skip, max_steps_per_zone, world_name)
        self.max_steps = max_steps_per_zone
        self.max_packets = max_packets
        self.forced_flags = forced_flags
        self.disconnect_after = disconnect_after

        self.handshake_status: Optional[int] = None
        self.handshake_message = ""
        self.error: Optional[BaseException] = None
        self.packets_sent = 0
        self.commands: List[List[int]] = []          # raw int32 commands per decision
        self.zone_commands: List[Tuple[int, int, tuple]] = []  # (packet_index, bot, zone)
        self.zone_starts: List[Tuple[int, int, tuple]] = []    # (packet_index, bot, zone) when a zone starts

        n = num_bots
        self.pos = np.zeros(n)
        self.last_action = np.zeros(n, dtype=np.int32)
        self.has_zone = [False] * n
        self.zone: List[Optional[tuple]] = [None] * n
        self.queued: List[Optional[tuple]] = [None] * n
        self.steps = [0] * n
        self.sock: Optional[socket.socket] = None

    # ------------------------------------------------------------------ socket helpers

    def _connect(self, timeout=10.0):
        deadline = time.time() + timeout
        while True:
            try:
                self.sock = socket.create_connection(("127.0.0.1", self.port), timeout=timeout)
                return
            except OSError:
                if time.time() > deadline:
                    raise
                time.sleep(0.02)

    def _recv_exact(self, n: int) -> bytes:
        buf = bytearray()
        while len(buf) < n:
            chunk = self.sock.recv(n - len(buf))
            if not chunk:
                raise ConnectionError("python closed the connection")
            buf += chunk
        return bytes(buf)

    # ------------------------------------------------------------------ simulation

    def run(self):
        try:
            self._connect()
            self.sock.sendall(self.header.to_bytes())
            status, length = struct.unpack("<ii", self._recv_exact(8))
            self.handshake_message = self._recv_exact(length).decode()
            self.handshake_status = status
            if status != P.HANDSHAKE_OK:
                return
            for i in range(self.num_bots):
                has, *zone = struct.unpack("<iffff", self._recv_exact(20))
                if has:
                    self.queued[i] = tuple(zone)
            while self.max_packets is None or self.packets_sent < self.max_packets:
                if self.disconnect_after is not None and self.packets_sent >= self.disconnect_after:
                    break
                self._decision()
        except (ConnectionError, OSError) as e:
            if self.handshake_status is None:
                self.error = e
        except BaseException as e:  # surfaced by tests
            self.error = e
        finally:
            if self.sock:
                self.sock.close()

    def _zone_target(self, i):
        return self.zone[i][2] if self.has_zone[i] else None

    def _obs(self, i, k):
        stats = np.zeros(P.STATS_SIZE, dtype=np.float32)
        stats[0] = self.last_action[i]
        stats[1] = k
        stats[2] = self.zone[i][0] if self.has_zone[i] else 0.0
        stats[3] = self.pos[i]
        vision = np.zeros((P.GRID_SIDE, P.GRID_SIDE), dtype=np.float32)
        vision[0, 0] = k
        return stats, vision

    def _start_zone(self, i, zone, k):
        self.zone[i] = zone
        self.has_zone[i] = True
        self.steps[i] = 0
        self.pos[i] = zone[0]
        self.zone_starts.append((k, i, zone))

    def _decision(self):
        k = self.packets_sent
        if k > 0:  # game ticks since the last decision used the last action
            for i in range(self.num_bots):
                a = self.last_action[i]
                self.pos[i] += (1 if a & P.ACTION_RIGHT else 0) - (1 if a & P.ACTION_LEFT else 0)

        flags = [0] * self.num_bots
        term_stats, term_vision = [], []
        for i in range(self.num_bots):
            f = 0
            if self.has_zone[i]:
                self.steps[i] += 1
                if self.pos[i] == self._zone_target(i):
                    f = P.FLAG_DONE | P.FLAG_WIN
                elif self.steps[i] >= self.max_steps:
                    f = P.FLAG_DONE | P.FLAG_TIMEOUT
            if self.forced_flags is not None:
                f |= self.forced_flags(i, k, self)
            if f & P.FLAG_DONE:
                s, v = self._obs(i, k)
                term_stats.append(s)
                term_vision.append(v)
                if self.queued[i] is not None:
                    self._start_zone(i, self.queued[i], k)
                    self.queued[i] = None
                    f |= P.FLAG_ZONE_SWITCHED
            elif not self.has_zone[i] and self.queued[i] is not None:
                self._start_zone(i, self.queued[i], k)
                self.queued[i] = None
                f |= P.FLAG_ZONE_SWITCHED
            flags[i] = f

        obs = [self._obs(i, k) for i in range(self.num_bots)]
        dists = [abs(self.pos[i] - self._zone_target(i)) if self.has_zone[i] else P.NO_DISTANCE
                 for i in range(self.num_bots)]
        packet = P.encode_packet([o[0] for o in obs], [o[1] for o in obs], dists, flags, term_stats, term_vision)
        self.sock.sendall(packet)
        self.packets_sent += 1

        cmds = []
        for i in range(self.num_bots):
            (cmd,) = struct.unpack("<i", self._recv_exact(4))
            cmds.append(cmd)
            if cmd >= P.ZONE_COMMAND_OFFSET:
                cmd -= P.ZONE_COMMAND_OFFSET
                zone = struct.unpack("<ffff", self._recv_exact(16))
                self.queued[i] = zone
                self.zone_commands.append((k, i, zone))
            self.last_action[i] = cmd & 0b111
        self.commands.append(cmds)


def write_zone_db(path, zones: Dict[str, tuple], world="aTestingSP2"):
    """zones: id -> (spawn_x, spawn_y, target_x, target_y)."""
    import json
    data = {"Zones": [{
        "ZoneId": zid,
        "OriginalLocation": {"SpawnX": z[0], "SpawnY": z[1], "TargetX": z[2], "TargetY": z[3], "WorldName": world},
        "Difficulty": 5, "DistanceTiles": abs(z[2] - z[0]) / 16, "HeightDelta": 0,
        "Stats": {"TimesTrained": 0, "SuccessfulAttempts": 0, "FailedAttempts": 0},
    } for zid, z in zones.items()]}
    with open(path, "w") as f:
        json.dump(data, f)
    return str(path)
