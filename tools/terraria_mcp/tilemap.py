"""Render the bridge's text tile grid (the `tiles` command) as a small PNG: cheaper than a screenshot and available
on a server, which draws nothing."""

import io

from PIL import Image, ImageDraw

COLORS = {
    " ": (0, 0, 0),
    ".": (150, 200, 255),     # air
    "#": (110, 80, 55),       # solid
    "=": (190, 150, 90),      # platform
    "T": (60, 140, 60),       # tree
    "+": (120, 170, 110),     # other non-solid tile
    "~": (40, 90, 230),       # water
    "%": (255, 110, 20),      # lava
    "h": (230, 180, 30),      # honey
    "s": (210, 160, 240),     # shimmer
    "P": (255, 255, 255),     # local player
    "B": (255, 40, 40),       # bot
    "p": (255, 200, 200),     # other player
    "E": (180, 0, 180),       # hostile NPC
    "N": (255, 255, 0),       # friendly NPC
    "*": (0, 255, 255),       # item
}
ENTITY_CHARS = set("PBpEN*")
PATH_COLORS = [(255, 0, 255), (0, 230, 255), (255, 255, 0)]  # first path magenta, then cyan, yellow


def render(tiles: dict, scale: int = 6, paths: list = None, markers: list = None) -> bytes:
    """tiles: result of the `tiles` command. paths: optional lists of (x, y) tile positions drawn as magenta lines,
    e.g. planned paths or bot trajectories. markers: optional (x, y, (r, g, b)) tiles drawn as outlined squares,
    e.g. start and target. Returns PNG bytes."""
    rows = tiles["rows"]
    h, w = len(rows), len(rows[0]) if rows else 0
    image = Image.new("RGB", (w * scale, h * scale))
    draw = ImageDraw.Draw(image)
    for y, row in enumerate(rows):
        for x, char in enumerate(row):
            box = (x * scale, y * scale, (x + 1) * scale - 1, (y + 1) * scale - 1)
            if char in ENTITY_CHARS:
                draw.rectangle(box, fill=COLORS["."])
                draw.ellipse(box, fill=COLORS[char])
            else:
                draw.rectangle(box, fill=COLORS.get(char, (255, 0, 255)))
    for k, path in enumerate(paths or []):
        color = PATH_COLORS[k % len(PATH_COLORS)]
        points = [((px - tiles["x0"]) * scale + scale / 2, (py - tiles["y0"]) * scale + scale / 2) for px, py in path]
        if len(points) > 1:
            draw.line(points, fill=color, width=max(2, scale // 2))
    for mx, my, color in markers or []:
        x0, y0 = (mx - tiles["x0"]) * scale, (my - tiles["y0"]) * scale
        draw.rectangle((x0 - scale, y0 - scale, x0 + 2 * scale - 1, y0 + 2 * scale - 1), outline=color, width=max(2, scale // 2))
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()
