"""How much of each movement mechanic a world contains (above the underworld): liquid cells, cobwebs, platforms, doors,
ropes, slippery / sticky / fast / conveyor floors, slopes and half blocks, and the standable spots next to them.
Decides per category whether test and training scenarios can come from the real terrain or need templates.

    python -m navigate.world_inventory --worlds eval_01 eval_02 train_01
"""

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from terraria_mcp.game import GameManager  # noqa: E402

OUT = Path(__file__).resolve().parents[2] / "results" / "training"

CODE = r"""
var c = new System.Collections.Generic.Dictionary<string, int>();
void Add(string k) { c[k] = c.TryGetValue(k, out int v) ? v + 1 : 1; }
int yMax = Main.maxTilesY - 210;  // above the underworld
for (int x = 50; x < Main.maxTilesX - 50; x++) {
  for (int y = 50; y < yMax; y++) {
    var t = Main.tile[x, y];
    if (t.LiquidAmount > 32) {
      if (t.LiquidType == LiquidID.Water) Add("water cells"); else if (t.LiquidType == LiquidID.Honey) Add("honey cells");
      else if (t.LiquidType == LiquidID.Lava) Add("lava cells"); else if (t.LiquidType == LiquidID.Shimmer) Add("shimmer cells");
    }
    if (!t.HasUnactuatedTile) continue;
    ushort type = t.TileType;
    if (type == TileID.Cobweb) Add("cobwebs");
    if (TileID.Sets.Platforms[type]) Add("platform tiles");
    if (type == TileID.ClosedDoor || type == TileID.OpenDoor || type == TileID.TallGateClosed || type == TileID.TallGateOpen) Add("door tiles");
    if (Main.tileRope[type]) Add("rope or vine-rope tiles");
    if (!Main.tileSolid[type] || Main.tileSolidTop[type]) continue;
    // floor tiles a player could stand on (air above)
    bool floor = !Main.tile[x, y - 1].HasUnactuatedTile || !Main.tileSolid[Main.tile[x, y - 1].TileType];
    if (floor) {
      if (TileID.Sets.IceSkateSlippery[type]) Add("ice floor tiles");
      if (type == TileID.HoneyBlock) Add("honey block floor tiles");
      if (type == TileID.Asphalt) Add("asphalt floor tiles");
      if (type == TileID.ConveyorBeltLeft || type == TileID.ConveyorBeltRight) Add("conveyor floor tiles");
      if (type == TileID.Sand || type == TileID.Ebonsand || type == TileID.Crimsand || type == TileID.Pearlsand) Add("sand floor tiles");
      if (t.IsHalfBlock || t.Slope != SlopeType.Solid) Add("sloped or half floor tiles");
    }
  }
}
return c;
"""


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("--worlds", nargs="+", required=True)
    args = p.parse_args()
    games = GameManager()
    out = {}
    for world in args.worlds:
        name = f"inv_{world}"
        games.launch("server", world=world, name=name, bridge_port=7894)
        try:
            out[world] = games.call(name, "execute", code=CODE, timeout=900)["result"]
        finally:
            games.stop(name)
        print(world, json.dumps(dict(sorted(out[world].items()))), flush=True)
    (OUT / "world_inventory.json").write_text(json.dumps(out, indent=1))


if __name__ == "__main__":
    main()
