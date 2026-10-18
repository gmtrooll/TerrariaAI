# Movement Hazards

Everything in a world that changes how a player moves or survives, read from the decompiled tModLoader 1.4.4 source (Oct 18, 2026), and how Navigate should handle each.

**Code locations** are in `tml-reference/src/Terraria/`: P = `Player.cs`, C = `Collision.cs`, WG = `WorldGen.cs`, W = `Wiring.cs`, TID = `ID/TileID.cs`.

**Units:** px per tick (16 px = 1 tile, 60 ticks = 1 s). Base player values: 20 × 42 px, gravity 0.4, max fall speed 10, jump 15 ticks at 5.01, run speed 3.

**Handling column:**
- **planner**: the route model must account for it (reachability, time cost).
- **restore**: it changes the world when touched, so the environment must put it back.
- **avoid**: keep Navigate scenarios away from it for now (it needs entities or is out of scope).
- **seen**: the observation already shows it (tile features in `TileClasses.cs`), so the policy can learn it.
- **none**: nothing to do.

## Changes speed, jump or gravity

| Thing | Effect | Code | How common | Handling |
| --- | --- | --- | --- | --- |
| Cobweb | Speed capped at 1 then damped (×0.6), jump cancelled, falling capped at 1: about 30 ticks per tile instead of about 4. Breaks after 20–100 ticks of moving inside | P:21302 | Common underground | planner (time cost), restore, seen ("sticky") |
| Water (lava moves alike) | Gravity 0.2, max fall 5, jump 30 ticks at 6.01, movement ×0.5. Leaving water cuts the remaining jump to a fifth. Jumping needs standing or wall-sliding | P:23403, P:22066, P:26675 | Common | planner (cost ×2), seen (liquid byte) |
| Honey (liquid) | Gravity 0.1, max fall 3, movement ×0.25 | P:23381, P:22033 | Uncommon (jungle hives) | planner (cost ×4), seen |
| Shimmer | Gravity 0.15. Submerged: frozen, falls through blocks for up to 60 s | P:23365, P:26565 | Rare | avoid |
| Space (high altitude) | Gravity × (0.25 … 1) with height; falls don't count while the factor is below 0.8 | P:23579 | Sky islands | planner (jump height), seen (gravity value) |
| Ice | Acceleration ×0.7, slowdown ×0.1 (slides) | TID:104, P:25470 | Common in snow | seen ("slippery") |
| Sandstorm wind | Desert during a sandstorm, no back wall: push of about 0.06 px per tick per tick | P:24569, P:18518 | Uncommon | none for now (an event; events are off) |
| Auto step-up / step-down | Climbs 1 block or half block without jumping; snaps down 8–16 px | C:4258, C:4184 | — | planner (could treat 1-block steps as walking) |
| Ceiling hit | Vertical speed 0.01, jump cancelled | P:27029 | — | planner (jump arcs need head room) |
| Platforms | Holding down drops through **only when falling at 1 px/tick or less**; faster falls land on them | C:2450 | Common | planner (minor: a fast fall can't pass a platform) |
| Ropes, chains, vines | Climb by holding up/down (max 8 px/tick up) | P:18332, P:25031 | Player-placed (no worldgen found) | none |
| Bouncy blocks, conveyors, asphalt, frozen slime, honey block floors | Various | M:9687, C:4555 | Player-placed or special seeds | none |

## Blocks or opens passages

| Thing | Behaviour | Code | How common | Handling |
| --- | --- | --- | --- | --- |
| Doors, tall gates | Open automatically when walked into, **for the local player only**. Our bots are updated as the local player, so they open doors too. They close again behind | `GameContent/DoorOpeningHelper.cs`, P:23614 | Common | planner (closed doors count as solid today, so a door looks like a wall), restore (open/closed state) |
| Locked Lihzahrd door | Can't be opened | WG:34235 | 1 per world | none (already solid) |
| Trapdoors | Not auto-opened | W:1620 | Player-placed | none |
| Actuated tiles, bubble blocks, minecart track | No collision | C:2474 | Track common underground | none (already non-solid) |
| Boulders, rolling cactus (before triggering) | Solid 2×2 blocks | M:7727 | Common / uncommon | seen ("trap") |

## Damages or kills

| Thing | Damage | Code | How common | Handling |
| --- | --- | --- | --- | --- |
| Fall | Above 25 tiles: (tiles − 25) × 10. None when landing in liquid, on clouds, with wings, … | P:24324 | Common | **planner**: drops over about 25 tiles must not be routes unless they land in liquid or on clouds |
| Spikes, wooden spikes | 60 / 80 + bleeding | TID:455 | Dungeon / temple | seen ("contact_damage") |
| Thorns (corrupt, crimson, jungle) | 10–17; the thorn is destroyed | TID:460, P:27311 | Common in those biomes | seen, restore |
| Hot blocks (meteorite, hellstone) | Burning, 30 HP/s | C:3713 | Underworld / meteor | seen ("hot") |
| Sand over the body | Suffocation 20 HP/s after 5 ticks | P:27286 | Common | seen ("suffocate", "falling") |
| Lava | 80 + On Fire | P:26498 | Below the lava line | seen; Standable already rejects it near start/target |
| Drowning | About 23 s of breath (200, minus 1 per 7 ticks) | P:21705 | Common | planner (long underwater stretches), seen ("breath") |
| Trap projectiles (darts, spears, flames, boulders, explosives, geysers, rolling cactus) | 20–500 | W:1955, WG:42534 | Common underground | avoid (projectiles are entities; Navigate has none) |

## Changes the world when touched

| Thing | Trigger | Code | How common | Handling |
| --- | --- | --- | --- | --- |
| Cobweb | Moving inside 20–100 ticks | P:21308 | Common | restore |
| Thorns | When they hurt | P:27311 | Common | restore |
| Thin ice | Landing faster than 7 px/tick | P:21938 | Uncommon (snow caves) | restore |
| Cracked dungeon bricks | Moving fast over them; breaks a 3×3 patch | P:21820 | Common in dungeons | restore |
| Pressure plates → traps | Boulder traps lose their boulder; style-7 plates (boulder, sand traps) break; explosives blow up the area | C:3759, WG:5711, WG:30898 | ~210 traps on a small world | avoid + restore |
| Falling sand | When support is removed | WG:81278 | Common | restore |
| Doors | Open/close | — | Common | restore |
| Saving | The game saves the world on exit and autosave, so every change above ends up in the world file | — | Always | **never save in environment mode** |

## Global

- Surface wind reaches players only through the sandstorm buff; rain and blizzard are visual.
- Special seeds (For the Worthy, Remix, Don't Starve, Not the Bees) change rules; our worlds are normal seeds.
- No auto-jump setting in 1.4.4: the jump key must be released between jumps (only items give auto-jump).

## Proposed plan

1. **World integrity:**
   - never save worlds in environment mode;
   - keep a snapshot of the original tiles, and when an episode ends, put back changed tiles near its route that no bot is standing near;
   - clear stray projectiles;
   - regenerate the 8 worlds from their seeds.

   This one mechanism covers cobwebs, thorns, ice, bricks, doors, traps and sand.
2. **Planner costs and limits:**
   - cobweb cells cost about 8 tiles' worth;
   - water costs 2, honey 4;
   - no route may drop more than about 25 tiles unless it lands in liquid or on clouds;
   - closed doors count as passable sideways.
3. **Scenarios:** keep starts, targets and routes away from pressure plates and trap tiles; no shimmer.
4. **Later, with combat (entities on):** trap projectiles, rolling cactus.
