"""
Wire protocol between the tModLoader mod (C#) and the Python trainer.

Mirror of Common/Systems/RLProtocol.cs. Change both sides together and bump
PROTOCOL_VERSION (byte layout) or OBS_VERSION (observation layout / meaning).
All values are little-endian.

Handshake (once per connection):
  C# -> Py: uint32 MAGIC, int32 protocol_version, int32 obs_version, int32 num_bots,
            int32 stats_size, int32 grid_side, int32 frame_skip, int32 max_steps_per_zone,
            int32 world_name_len, utf8 world_name
  Py -> C#: int32 status (0 = ok), int32 msg_len, utf8 msg,
            then if status == 0: num_bots x (int32 has_zone, float32 spawnX, spawnY, targetX, targetY)

Decision tick (C# -> Py), N = num_bots:
  N x STATS_SIZE float32 | N x GRID_CELLS float32 | N x (float32 path_dist, uint8 flags)
  | for every bot with FLAG_DONE, in bot order: STATS_SIZE + GRID_CELLS float32 (terminal obs)
Reply (Py -> C#), per bot: int32 command. If command >= ZONE_COMMAND_OFFSET the action is
  command - ZONE_COMMAND_OFFSET and 4 float32 follow (spawnX, spawnY, targetX, targetY, pixels):
  the zone that bot moves to when its current episode ends.

C# sends a packet, then blocks reading actions, then applies them. So the packet Python
receives after sending a_t describes the state *after* a_t, and reset() must read the very
first packet (sent before any action).
"""
import struct
from dataclasses import dataclass
from typing import Dict, List, Optional, Sequence, Tuple

import numpy as np

MAGIC = 0x4C524954  # bytes "TIRL"
PROTOCOL_VERSION = 2
OBS_VERSION = 2

# Observation layout, OBS_VERSION 2 (RLInterface.GetObservation). Values are scaled to about [-1, 1].
STATS_FIELDS: Tuple[str, ...] = (
    "vel_x", "vel_y", "direction", "subtile_x", "subtile_y",
    "jump_frames", "tsunami_available", "tsunami_active",
    "wet", "honey_wet", "lava_wet", "frozen", "webbed", "stoned",
    "fall_distance",
    "life_fraction", "immune_time",
    "has_target", "target_dx", "target_dy", "target_dir_x", "target_dir_y", "target_log_dist",
)
STATS_SIZE = len(STATS_FIELDS)  # 23
GRID_SIDE = 40
GRID_CELLS = GRID_SIDE * GRID_SIDE

FLOAT_DTYPE = np.dtype("<f4")
STATS_BYTES = STATS_SIZE * 4
GRID_BYTES = GRID_CELLS * 4
OBS_BYTES = STATS_BYTES + GRID_BYTES
EXTRA_BYTES = 5  # float32 path_dist + uint8 flags

ZONE_COMMAND_OFFSET = 1_000_000
ACTION_LEFT, ACTION_RIGHT, ACTION_JUMP = 1, 2, 4

HANDSHAKE_OK = 0
HANDSHAKE_REJECTED = 1

FLAG_DONE = 1
FLAG_WIN = 2
FLAG_DIE = 4
FLAG_TIMEOUT = 8
FLAG_ABORTED = 16        # episode ended without an outcome (bot despawned, training resumed)
FLAG_ZONE_SWITCHED = 32  # bot moved to its queued zone this tick

NO_DISTANCE = -1.0

HEADER = struct.Struct("<Iiiiiiiii")
REPLY_HEAD = struct.Struct("<ii")
ZONE_ENTRY = struct.Struct("<iffff")
ACTION_ONLY = struct.Struct("<i")
ACTION_WITH_ZONE = struct.Struct("<iffff")

Zone = Tuple[float, float, float, float]  # spawn_x, spawn_y, target_x, target_y (pixels)


class ProtocolError(RuntimeError):
    """The byte stream from Terraria did not match the protocol."""


class HandshakeError(ProtocolError):
    """C# and Python disagree on the protocol or configuration."""


class TerrariaConnectionError(ProtocolError):
    """The connection to Terraria was lost. Training must stop: no fabricated data is returned."""


@dataclass
class Handshake:
    magic: int
    protocol_version: int
    obs_version: int
    num_bots: int
    stats_size: int
    grid_side: int
    frame_skip: int
    max_steps_per_zone: int
    world_name: str

    def to_bytes(self) -> bytes:
        name = self.world_name.encode("utf-8")
        return HEADER.pack(self.magic, self.protocol_version, self.obs_version, self.num_bots,
                           self.stats_size, self.grid_side, self.frame_skip, self.max_steps_per_zone,
                           len(name)) + name


def validate_handshake(h: Handshake, expected_num_bots: int) -> List[str]:
    """Return human-readable problems; empty list means the two sides agree."""
    if h.magic != MAGIC:
        return [f"bad magic 0x{h.magic:08X} (expected 0x{MAGIC:08X}): the client is not the "
                f"FirstMod_Sp mod, or it is a mod build from before the protocol handshake existed"]
    problems = []
    if h.protocol_version != PROTOCOL_VERSION:
        problems.append(f"protocol version: mod={h.protocol_version}, python={PROTOCOL_VERSION} "
                        f"(rebuild the mod or update the Training folder so both match)")
    if h.obs_version != OBS_VERSION:
        problems.append(f"observation version: mod={h.obs_version}, python={OBS_VERSION}")
    if h.stats_size != STATS_SIZE:
        problems.append(f"stats size: mod={h.stats_size}, python={STATS_SIZE}")
    if h.grid_side != GRID_SIDE:
        problems.append(f"grid size: mod={h.grid_side}, python={GRID_SIDE}")
    if h.num_bots != expected_num_bots:
        problems.append(f"num_bots: mod={h.num_bots} (from the training_config.json the mod loaded), "
                        f"python={expected_num_bots} (from config / --num-bots / dashboard slider)")
    return problems


def encode_handshake_reply(status: int, message: str, zones: Sequence[Optional[Zone]] = ()) -> bytes:
    msg = message.encode("utf-8")
    out = bytearray(REPLY_HEAD.pack(status, len(msg)))
    out += msg
    if status == HANDSHAKE_OK:
        for zone in zones:
            if zone is None:
                out += ZONE_ENTRY.pack(0, 0.0, 0.0, 0.0, 0.0)
            else:
                out += ZONE_ENTRY.pack(1, *zone)
    return bytes(out)


def action_bits(action) -> int:
    """MultiBinary(3) action [left, right, jump] -> bitmask."""
    bits = 0
    if action[0] > 0.5:
        bits |= ACTION_LEFT
    if action[1] > 0.5:
        bits |= ACTION_RIGHT
    if action[2] > 0.5:
        bits |= ACTION_JUMP
    return bits


def encode_actions(actions, zones_to_send: Dict[int, Zone]) -> bytes:
    """One command per bot; bots in zones_to_send also carry their next (queued) zone."""
    out = bytearray()
    for i, act in enumerate(actions):
        bits = action_bits(act)
        zone = zones_to_send.get(i)
        if zone is None:
            out += ACTION_ONLY.pack(bits)
        else:
            out += ACTION_WITH_ZONE.pack(ZONE_COMMAND_OFFSET + bits, *zone)
    return bytes(out)


def head_size(num_bots: int) -> int:
    """Bytes of the fixed part of a decision packet."""
    return num_bots * (OBS_BYTES + EXTRA_BYTES)


def parse_head(buf, num_bots: int):
    """Split the fixed part of a packet into (stats, vision, path_dist, flags) arrays (copies)."""
    offset = 0
    stats = np.frombuffer(buf, dtype=FLOAT_DTYPE, count=num_bots * STATS_SIZE, offset=offset)
    stats = stats.reshape(num_bots, STATS_SIZE).astype(np.float32)
    offset += num_bots * STATS_BYTES
    vision = np.frombuffer(buf, dtype=FLOAT_DTYPE, count=num_bots * GRID_CELLS, offset=offset)
    vision = vision.reshape(num_bots, 1, GRID_SIDE, GRID_SIDE).astype(np.float32)
    offset += num_bots * GRID_BYTES
    extra = np.frombuffer(buf, dtype=np.dtype([("dist", "<f4"), ("flags", "u1")]), count=num_bots, offset=offset)
    return stats, vision, extra["dist"].astype(np.float32), extra["flags"].astype(np.uint8)


def parse_terminal(buf, count: int):
    """Terminal observations block: count x (stats, grid)."""
    stats = np.empty((count, STATS_SIZE), dtype=np.float32)
    vision = np.empty((count, 1, GRID_SIDE, GRID_SIDE), dtype=np.float32)
    for k in range(count):
        base = k * OBS_BYTES
        stats[k] = np.frombuffer(buf, dtype=FLOAT_DTYPE, count=STATS_SIZE, offset=base)
        vision[k, 0] = np.frombuffer(buf, dtype=FLOAT_DTYPE, count=GRID_CELLS,
                                     offset=base + STATS_BYTES).reshape(GRID_SIDE, GRID_SIDE)
    return stats, vision


def encode_packet(stats, vision, dists, flags, terminal_stats=None, terminal_vision=None) -> bytes:
    """Build a C#-style packet (used by tests / fake server)."""
    stats = np.asarray(stats, dtype=FLOAT_DTYPE)
    vision = np.asarray(vision, dtype=FLOAT_DTYPE)
    out = bytearray(stats.tobytes())
    out += vision.tobytes()
    for d, f in zip(dists, flags):
        out += struct.pack("<fB", float(d), int(f))
    if terminal_stats is not None:
        for s, v in zip(terminal_stats, terminal_vision):
            out += np.asarray(s, dtype=FLOAT_DTYPE).tobytes()
            out += np.asarray(v, dtype=FLOAT_DTYPE).tobytes()
    return bytes(out)
