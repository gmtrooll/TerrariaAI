"""Decode and draw the mod's observation (Common/Observation, docs/Observation Design.md).

The `observe` bridge command returns the grids as base64 and the vectors as named values; `observation_spec`
returns the layout and the tile class table. `render` draws what the bot sees, for checking the observation
against the game (compare with a screenshot or render_map of the same spot).
"""

import base64
import io

import numpy as np
from PIL import Image, ImageDraw

LIQUID_COLORS = [(40, 90, 230), (255, 110, 20), (230, 180, 30), (210, 160, 240)]  # water, lava, honey, shimmer


def decode(observation: dict) -> dict:
    """Grids as numpy arrays: fine (H, W, 4) and coarse (h, w, 4) uint8."""
    layout = observation["layout"]
    fine = np.frombuffer(base64.b64decode(observation["fine"]), np.uint8)
    fine = fine.reshape(layout["fine"]["h"], layout["fine"]["w"], 4)
    coarse = None
    if observation.get("coarse"):
        c = layout["coarse"]
        coarse = np.frombuffer(base64.b64decode(observation["coarse"]), np.uint8).reshape(c["h"], c["w"], 4)
    return {"fine": fine, "coarse": coarse}


def _class_color(features: dict) -> tuple:
    if features.get("contact_damage") or features.get("hot") or features.get("trap"):
        return (220, 30, 30)
    if features.get("rope"):
        return (230, 220, 60)
    if features.get("door_closed") or features.get("door_open"):
        return (150, 90, 30)
    if features.get("platform"):
        return (200, 160, 100)
    if features.get("solid"):
        if features.get("slippery"):
            return (170, 220, 255)
        if features.get("sticky"):
            return (240, 200, 40)
        return (120, 85, 55)
    if features.get("tree"):
        return (60, 140, 60)
    return (150, 190, 140)  # non-solid tile: plants, torches, furniture parts


def render(observation: dict, spec: dict, scale: int = 8) -> bytes:
    """PNG of the fine grid (left) and, if present, the coarse overview (right, same height)."""
    grids = decode(observation)
    fine = grids["fine"]
    names = spec["class_feature_names"]
    classes = [{n: v for n, v in zip(names, row) if v} for row in spec["classes"]]
    h, w = fine.shape[:2]
    image = Image.new("RGB", (w * scale, h * scale), (0, 0, 0))
    draw = ImageDraw.Draw(image)
    for y in range(h):
        for x in range(w):
            cls, shape, liquid, flags = (int(v) for v in fine[y, x])
            x0, y0, x1, y1 = x * scale, y * scale, (x + 1) * scale - 1, (y + 1) * scale - 1
            visible, remembered, outside = flags & 2, flags & 4, flags & 16
            if outside:
                draw.rectangle((x0, y0, x1, y1), fill=(40, 40, 40))
                continue
            if not visible and not remembered:
                continue  # unknown: black
            background = (150, 200, 255) if not flags & 1 else (110, 120, 140)  # sky vs wall behind
            draw.rectangle((x0, y0, x1, y1), fill=background)
            if liquid:
                amount, kind = (liquid & 63) / 63, liquid >> 6
                top = y1 - int(amount * scale)
                draw.rectangle((x0, top, x1, y1), fill=LIQUID_COLORS[kind])
            if cls:
                color = _class_color(classes[cls])
                if shape == 0:
                    draw.rectangle((x0, y0, x1, y1), fill=color)
                elif shape == 1:  # half block: lower half
                    draw.rectangle((x0, y0 + scale // 2, x1, y1), fill=color)
                else:  # slopes 2-5: SlopeDownLeft, SlopeDownRight, SlopeUpLeft, SlopeUpRight (solid corner sides)
                    corners = {2: [(x0, y1), (x1, y1), (x0, y0)], 3: [(x0, y1), (x1, y1), (x1, y0)],
                               4: [(x0, y0), (x1, y0), (x0, y1)], 5: [(x0, y0), (x1, y0), (x1, y1)]}[shape]
                    draw.polygon(corners, fill=color)
                if flags & 8:  # locked door
                    draw.line((x0, y0, x1, y1), fill=(255, 255, 255))
            if remembered and not visible:  # seen before, not now: darkened
                image.paste((0, 0, 0), (x0, y0, x1 + 1, y1 + 1), mask=Image.new("L", (scale, scale), 110))
    # The bot sits at the centre cell (integer division, as in the mod).
    cx, cy = w // 2, h // 2
    draw.rectangle((cx * scale, cy * scale, (cx + 1) * scale - 1, (cy + 1) * scale - 1), outline=(255, 0, 0), width=2)
    goal = observation.get("goal") or {}
    if goal.get("has_target"):
        gx, gy = cx + goal["dx"] * 20, cy + goal["dy"] * 20
        draw.line(((cx + 0.5) * scale, (cy + 0.5) * scale, (gx + 0.5) * scale, (gy + 0.5) * scale), fill=(255, 0, 255), width=2)

    coarse = grids["coarse"]
    if coarse is not None:
        ch, cw = coarse.shape[:2]
        cscale = max(1, (h * scale) // ch)
        overview = Image.new("RGB", (cw * cscale, ch * cscale), (0, 0, 0))
        odraw = ImageDraw.Draw(overview)
        for y in range(ch):
            for x in range(cw):
                solid, platform, bits, seen = (int(v) for v in coarse[y, x])
                if not seen:
                    continue
                shade = int(220 - 180 * solid / 16)
                color = [shade, shade, shade]
                for kind in range(4):
                    if bits & (1 << kind):
                        color = [int(0.5 * a + 0.5 * b) for a, b in zip(color, LIQUID_COLORS[kind])]
                if bits & 16:
                    color = [255, 60, 60]
                fade = 0.35 + 0.65 * seen / 16
                odraw.rectangle((x * cscale, y * cscale, (x + 1) * cscale - 1, (y + 1) * cscale - 1),
                                fill=tuple(int(c * fade) for c in color))
        # Outline the fine grid's area inside the overview.
        fw, fh = w / 4, h / 4
        ox, oy = cw / 2 - fw / 2, ch / 2 - fh / 2
        odraw.rectangle((ox * cscale, oy * cscale, (ox + fw) * cscale, (oy + fh) * cscale), outline=(255, 0, 0))
        combined = Image.new("RGB", (image.width + 8 + overview.width, max(image.height, overview.height)))
        combined.paste(image, (0, 0))
        combined.paste(overview, (image.width + 8, 0))
        image = combined
    buffer = io.BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()
