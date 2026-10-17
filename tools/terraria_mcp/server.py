"""Terraria MCP server: lets an agent build the mod, launch tModLoader (client or headless server), act in the game,
inspect it and run C# inside it. Talks to the mod's automation bridge (Common/Automation).

Run: tools/.venv/Scripts/python.exe -m terraria_mcp.server   (registered in the repository's .mcp.json)
"""

import io
import json
import sys
from pathlib import Path

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
    __package__ = "terraria_mcp"

from mcp.server.fastmcp import FastMCP, Image
from PIL import Image as PILImage

from terraria_mcp import config, tilemap
from terraria_mcp.game import GameManager

mcp = FastMCP(
    "terraria",
    instructions=(
        "Drive tModLoader for the FirstMod_Sp project. Typical loop: build_mod, launch (client shows a window; "
        "server is headless and faster), then status / get_state / get_tiles / execute_csharp / input / step, "
        "and stop when done. Prefer get_state, get_tiles and render_map over screenshot: they are cheaper. "
        "Positions are in tiles (16 px). Commands that take 'instance' default to the only running instance. "
        "After changing C# code: rebuild_and_relaunch. Game exceptions are counted in status.errors; read them "
        "with errors()."
    ),
)
games = GameManager()


def _call(instance, cmd, timeout=30, **args):
    args = {k: v for k, v in args.items() if v is not None}
    return games.call(instance, cmd, timeout=timeout, **args)


def _target(bot, player):
    return {"bot": bot, "player": player}


# ---------- session ----------

@mcp.tool()
def build_mod() -> dict:
    """Build the mod (dotnet build) and install it into the training save directory. Running instances keep the
    old build until relaunched (see rebuild_and_relaunch)."""
    return games.build()


@mcp.tool()
def launch(mode: str = "client", world: str = config.DEFAULT_WORLD, bots: int = 0, name: str = None,
           timeout_s: float = 240) -> dict:
    """Start tModLoader and wait until it is in the world. mode: 'client' (window, local player 'Automation',
    screenshots) or 'server' (headless, no local player, faster). bots: fake players spawned at the world spawn.
    Uses the isolated training save directory; personal worlds and mods are never touched."""
    return games.launch(mode, world=world, bots=bots, name=name, timeout=timeout_s)


@mcp.tool()
def stop(instance: str = None, save: bool = False) -> dict:
    """Quit an instance (killed if it does not exit). save=True saves the world first."""
    return games.stop(instance, save=save)


@mcp.tool()
def stop_all() -> list:
    """Quit every instance started by this server."""
    return games.stop_all()


@mcp.tool()
def instances() -> list:
    """List instances with their game status. Also attaches to games still running from an earlier session."""
    games.attach_running()
    return games.status()


@mcp.tool()
def rebuild_and_relaunch(instance: str = None, timeout_s: float = 240) -> dict:
    """After a C# change: build the mod, then restart the instance with the same settings. If the build fails,
    the instance keeps running the old build and the errors are returned."""
    build = games.build()
    if not build["ok"]:
        return {"build": build, "relaunched": False}
    return {"build": build, "relaunched": games.relaunch(instance, timeout=timeout_s)}


@mcp.tool()
def logs(instance: str = None, tail: int = 50, grep: str = None) -> str:
    """Last lines of an instance's console output (world-generation progress lines are hidden). grep: regex filter."""
    return games.logs(instance, tail=tail, grep=grep)


@mcp.tool()
def worlds() -> list:
    """Worlds in the training save directory."""
    return games.list_worlds()


@mcp.tool()
def create_world(world: str, size: str = "small", seed: str = "42") -> dict:
    """Generate a world (small/medium/large) with a temporary headless server. Takes ~30 s for small."""
    return games.create_world(world, size=size, seed=seed)


# ---------- game ----------

@mcp.tool()
def status(instance: str = None) -> dict:
    """Game status: side, world, tick, paused, speed, bots, time of day, error count."""
    return _call(instance, "status")


@mcp.tool()
def execute_csharp(code: str, instance: str = None, timeout_s: float = 60) -> dict:
    """Compile and run C# inside the running game, on the game thread between ticks. The code is a method body
    with full access to Terraria, tModLoader and this mod; usings for System.Linq, Terraria, Terraria.ID,
    Microsoft.Xna.Framework, FirstMod_Sp.Common.Systems/Automation are included. A lone expression is returned;
    otherwise use 'return'. Helpers: Print(x), Me (local player), Bots (List<Player>), Bot(n).
    Return projections, not game objects: `return new { Me.Center, Me.statLife };`. First call compiles ~1.5 s."""
    return _call(instance, "execute", timeout=timeout_s, code=code)


@mcp.tool()
def wait_until(condition: str, instance: str = None, timeout_ticks: int = 3600, timeout_s: float = 60) -> dict:
    """Block until a C# boolean expression is true (checked after every tick), e.g. 'Bot(0).velocity.Y == 0'.
    Returns met=false on timeout."""
    return _call(instance, "wait_until", timeout=timeout_s + 5, condition=condition, timeout_ticks=timeout_ticks,
                 timeout_ms=int(timeout_s * 1000))


@mcp.tool()
def pause(instance: str = None) -> dict:
    """Freeze the world (no ticks run). Commands still work; use step to advance."""
    return _call(instance, "pause")


@mcp.tool()
def resume(instance: str = None) -> dict:
    """Unfreeze the world."""
    return _call(instance, "resume")


@mcp.tool()
def step(ticks: int = 1, instance: str = None, timeout_s: float = 60) -> dict:
    """Run N ticks and return when they are done. While paused they run back to back as fast as possible."""
    return _call(instance, "step", timeout=timeout_s + 5, ticks=ticks, timeout_ms=int(timeout_s * 1000))


@mcp.tool()
def set_speed(ticks_per_frame: int = 1, instance: str = None) -> dict:
    """Game speed: ticks per frame (1 = normal 60 ticks/s)."""
    return _call(instance, "speed", ticks_per_frame=ticks_per_frame)


@mcp.tool()
def get_state(bot: int = None, player: int = None, all: bool = False, instance: str = None) -> dict:
    """A player's position, velocity, life, hotbar. bot: index into the bot list; player: raw slot; default: the
    local player (bot 0 on a server). all=True: local player and every bot."""
    return _call(instance, "state", all=all or None, **_target(bot, player))


@mcp.tool()
def get_tiles(x: int = None, y: int = None, w: int = 80, h: int = 45, bot: int = None, player: int = None,
              instance: str = None) -> str:
    """The tiles in a region as text, one row per line (top-left at x,y; or centred on a player). Legend: '.' air,
    '#' solid, '=' platform, 'T' tree, '+' non-solid tile, '~' water, '%' lava, 'h' honey, 's' shimmer,
    'P' local player, 'B' bot, 'p' player, 'E' hostile NPC, 'N' friendly NPC, '*' item, ' ' outside the world."""
    tiles = _call(instance, "tiles", x=x, y=y, w=w, h=h, **_target(bot, player))
    header = f"top-left tile ({tiles['x0']}, {tiles['y0']}), {tiles['w']}x{tiles['h']}"
    return header + "\n" + "\n".join(tiles["rows"])


@mcp.tool()
def render_map(x: int = None, y: int = None, w: int = 120, h: int = 68, scale: int = 5, bot: int = None,
               player: int = None, instance: str = None) -> Image:
    """A colour image of a tile region drawn from game data (works on a headless server, cheaper than a
    screenshot). Same region arguments as get_tiles."""
    tiles = _call(instance, "tiles", x=x, y=y, w=w, h=h, **_target(bot, player))
    return Image(data=tilemap.render(tiles, scale=scale), format="png")


@mcp.tool()
def screenshot(scale: float = 0.5, instance: str = None) -> Image:
    """The game window as an image (client only). scale < 1 shrinks it to save tokens."""
    shot = _call(instance, "screenshot", timeout=15)
    image = PILImage.open(shot["path"]).convert("RGB")
    if scale != 1:
        image = image.resize((max(1, int(image.width * scale)), max(1, int(image.height * scale))))
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return Image(data=buffer.getvalue(), format="png")


@mcp.tool()
def get_entities(x: float = None, y: float = None, radius: float = 50, bot: int = None, player: int = None,
                 instance: str = None) -> dict:
    """NPCs, dropped items and projectiles within radius tiles of a point or player."""
    return _call(instance, "entities", x=x, y=y, radius=radius, **_target(bot, player))


@mcp.tool()
def input(left: bool = False, right: bool = False, up: bool = False, down: bool = False, jump: bool = False,
          hook: bool = False, use: bool = False, aim_x: float = None, aim_y: float = None, ticks: int = 1,
          wait: bool = True, release: bool = False, bot: int = None, player: int = None,
          instance: str = None) -> dict:
    """Hold controls for N ticks on the local player or a bot (injected in the game, works unfocused and on a
    server). aim_x/aim_y: world tile to aim item use at. wait=True returns the player state after the ticks ran.
    release=True drops a held input."""
    aim = {"x": aim_x, "y": aim_y} if aim_x is not None and aim_y is not None else None
    return _call(instance, "input", timeout=max(30, ticks / 30 + 10), left=left, right=right, up=up, down=down,
                 jump=jump, hook=hook, use=use, aim=aim, ticks=ticks, wait=wait, release=release or None,
                 **_target(bot, player))


@mcp.tool()
def spawn_bots(count: int = 1, x: float = None, y: float = None, spread: float = 2, instance: str = None) -> dict:
    """Spawn fake-player bots at a tile position (default: just above the world spawn), spread horizontally."""
    return _call(instance, "spawn_bots", count=count, x=x, y=y, spread=spread)


@mcp.tool()
def despawn_bots(instance: str = None) -> dict:
    """Remove every bot."""
    return _call(instance, "despawn_bots")


@mcp.tool()
def teleport(x: float, y: float, bot: int = None, player: int = None, instance: str = None) -> dict:
    """Move a player to a tile position."""
    return _call(instance, "teleport", x=x, y=y, **_target(bot, player))


@mcp.tool()
def give(item: str, stack: int = 1, bot: int = None, player: int = None, instance: str = None) -> dict:
    """Put an item in a player's inventory. item: ItemID name (e.g. 'IronPickaxe') or numeric id."""
    return _call(instance, "give", item=int(item) if item.isdigit() else item, stack=stack, **_target(bot, player))


@mcp.tool()
def spawn_npc(npc: str, x: float = None, y: float = None, bot: int = None, player: int = None,
              instance: str = None) -> dict:
    """Spawn an NPC at a tile position (default: 10 tiles right of the player). npc: NPCID name or numeric id."""
    return _call(instance, "spawn_npc", npc=int(npc) if npc.isdigit() else npc, x=x, y=y, **_target(bot, player))


@mcp.tool()
def set_time(time: float = None, day: bool = None, instance: str = None) -> dict:
    """Set the time of day (ticks since dawn or dusk; day is 0-54000, night 0-32400)."""
    return _call(instance, "set_time", time=time, day=day)


@mcp.tool()
def errors(clear: bool = False, instance: str = None) -> dict:
    """Exceptions thrown inside game updates since launch (grouped, with stack excerpts)."""
    return _call(instance, "errors", clear=clear or None)


# ---------- measurement ----------

@mcp.tool()
def run_bench(mode: str = "server", bots: str = "4", processes: str = "1", repeats: int = 1, ticks: int = 3000,
              frame_skip: int = 4, observe: bool = False, label: str = "", notes: str = "") -> list:
    """Throughput benchmark: for each (processes, bots) combination, launch fresh instances, run warmup + N ticks
    back to back with a random policy, record ticks/s, decisions/s, per-section ms, us per bot tick, CPU, RAM and
    allocations. bots/processes: space-separated lists, e.g. "1 4 16". Rows are appended to results/bench/bench.csv.
    Takes ~10-30 s per configuration."""
    from terraria_mcp import bench
    return bench.run_matrix(mode, bots_list=[int(b) for b in bots.split()],
                            processes_list=[int(p) for p in processes.split()], frame_skip=frame_skip, ticks=ticks,
                            observe=observe, repeats=repeats, label=label, notes=notes, games=games)


@mcp.tool()
def physics_check(modes: str = "server client", bots: int = 8, ticks: int = 2400) -> dict:
    """Run the same start positions and action sequence twice in each mode (enemies cleared, spawns blocked) and
    report the maximum trajectory deviation in tiles between every pair of runs. 0.0 = bit-identical physics."""
    from terraria_mcp import bench
    return bench.physics_check(tuple(modes.split()), bots=bots, ticks=ticks, games=games)


@mcp.tool()
def profile(mode: str = "server", bots: int = 16, seconds: int = 8, root: str = None, top: int = 30) -> dict:
    """CPU profile (dotnet-trace sampling) of a fresh instance running the benchmark. Returns the methods with the
    most inclusive/exclusive time on the game thread; root (substring, e.g. "Terraria.Player:Update") limits it to
    one subtree and adds its callers."""
    from terraria_mcp import profile as profiler
    return profiler.profile_bench(mode, bots, seconds, root, top=top, games=games)


@mcp.tool()
def bridge_command(cmd: str, args_json: str = "{}", instance: str = None, timeout_s: float = 30) -> object:
    """Send any bridge command directly (see the mod's BridgeCommands.cs); args_json is a JSON object."""
    return _call(instance, cmd, timeout=timeout_s, **json.loads(args_json))


def main():
    try:
        mcp.run()
    finally:
        games.stop_all()


if __name__ == "__main__":
    main()
