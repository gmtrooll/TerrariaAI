# Observation Design

Oct 18, 2026

What the agent sees each decision. Everything in this document comes from the decompiled game code in `Documents/My Games/Terraria/tml-reference/src/Terraria`, with file and line references where a claim depends on them. It sets out the options and makes recommendations for discussion; nothing is final.

The observation has five parts:

1. **Tile grid** around the bot.
2. **Self state:** what the bot is doing right now.
3. **Capabilities:** what the bot can do with its current gear.
4. **Entities:** enemies, projectiles, items.
5. **Goal:** set by the skill being run.

## Principles

1. **Enough to predict the next moment.** The policy must be able to tell what its own action will do. Any hidden game state that changes the outcome of an action belongs in the observation, or must be inferable from recent history. Examples: how many ticks of jump are left, wing time, whether an extra jump is still available. This property is called being "Markov": the observation alone, without memory, decides what happens next.
2. **Fair information.** Only what a human player could see or know. This keeps the result honest ("the agent plays the game", not "the agent reads memory") and keeps the thesis claims clean. Where full fairness is expensive, the gap is stated, not hidden.
3. **Cheap to compute.** The mod builds the observation for every bot every decision, on the same CPU core that runs the game. It must stay small next to the ~80 µs a bot costs per decision (20 µs per tick × 4 ticks).
4. **Compact on the wire, expanded on the GPU.** The mod sends small integer codes. The learner turns them into the network's input channels on the GPU (see "Size and cost").
5. **One layout for all skills.** Navigate uses a subset; later skills switch on more channels without changing the format.

## What the current observation lacks

Today the bot gets a 40×40 grid with three values (air 0, platform 0.5, solid 1) and 23 stats. Measured against the game code:

- **No slopes or half blocks.** A sloped tile or a half block reads as a full solid block, but the game treats them very differently. You walk up a slope without jumping. A half block raises a step by only 8 px (`Collision.SlopeCollision`, `Collision.cs:1979`; `Collision.StepUp`, `Collision.cs:4258`). The bot cannot predict its own movement there.
- **No liquids.** Water halves the bot's displacement, honey quarters it (`Player.cs:22070-22086`), and lava burns.
- **No hazards.** Spikes, thorns, hot blocks, traps and lava look like ordinary tiles.
- **No ropes or doors.** Ropes are climbable (`Main.tileRope`); closed doors are solid but open automatically.
- **Hidden jump state is partly missing.** The stats cover the base jump and the Tsunami bottle, but not wing time, rocket time, dash cooldown or remaining fall height.
- **Short view.** At 3 px/tick a bot covers about 11 tiles per second. With Terraspark-type boots (6.75 px/tick) it covers about 25. A 20-tile radius is less than one second of look-ahead at speed.

## 1. The tile grid

### How much to show

What a player sees (`Main.cs`, camera and zoom code):

| Screen | Visible tiles |
| --- | --- |
| 1920×1080 at the default zoom (1×, also the minimum) | 120 × 68 |
| 1920×1080 at maximum zoom (2×) | 60 × 34 |
| Largest view the game allows (1920×1200, also on bigger monitors) | 120 × 75 |
| Minimum resolution (800×720) | 50 × 45 |

The camera is centred on the player, with no look-ahead (a few scopes pan it). Near a world edge it stops scrolling instead of showing past the edge.

Three options:

| Option | Cells | For | Against |
| --- | --- | --- | --- |
| A. Keep 40×40 | 1,600 | Cheapest | Less than a second of look-ahead at speed; a sixth of what a player sees |
| B. Full view, 120×68 at one cell per tile | 8,160 | Matches a player exactly | 5× the cells: CNN compute, wire size and mod time all grow about 5× |
| C. Two resolutions | ~2,200 | Detail near the bot, overview far away | Two grids for the network instead of one |

Option C is what an eye does: sharp in the centre, coarse at the edge (also called "foveated"). Proposed sizes:

- **Fine grid:** 41×41 tiles around the bot at one cell per tile. This covers the jump arc (6 tiles up, 12–18 tiles forward at running speed) and the whole mining and placing reach (about ±6 by +4/−2 tiles, `Player.cs:46572`).
- **Coarse grid:** the full 120×68 view, at one cell per 4×4 tiles (30×17 cells). Each coarse cell summarises its 16 tiles: the fraction that is solid, whether any liquid or hazard is present, and so on.

**Recommendation: C.** It gives the agent the player's full field of view at a quarter of option B's cost. Whether the coarse grid actually helps is an ablation (train with and without it).

### What each cell holds

The game stores about 15 bytes per tile (`Tile.cs`, `TileData.cs`):

- the tile type and the wall type
- whether a block is present, and whether it is actuated (switched off by wiring, and then not solid)
- the slope shape or half block
- the liquid amount and type
- wires, paint and the frame (which part of a multi-tile object this is)

Behaviour is defined per tile **type** in static tables (`Main.tileSolid`, `Main.tileSolidTop`, `Main.tileRope`, `TileID.Sets.*`). There is no single "category" field, so the mod builds a lookup table once at load: tile type to a set of properties. Per cell, it then combines that row with the tile's own shape and liquid.

Proposed channels:

| Group | Channel | Values | Source | Skills |
| --- | --- | --- | --- | --- |
| Collision | Solid block | yes/no; actuated blocks count as not solid | `Tile.HasUnactuatedTile`, `Main.tileSolid` | all |
| | Platform (one-way, drop through with down) | yes/no | `Main.tileSolidTop`; only the top row of furniture counts (`Collision.cs:2540`) | all |
| | Shape | full, half block, 4 slope directions | `Tile.BlockType` | all |
| | Climbable (rope, chain, vine rope) | yes/no | `Main.tileRope`, `WorldGen.IsRope` | all |
| | Door | closed, open, locked | door IDs, `WorldGen.IsLockedDoor` | all |
| Liquid | Amount | 0–1 | `Tile.LiquidAmount` | all |
| | Type | water, lava, honey, shimmer | `Tile.LiquidType` | all |
| Hazard | Contact damage | 0–1 (damage ÷ 100: cactus 6 … spikes 60, wooden spikes 80) | `TileID.Sets.TouchDamageImmediate` | all |
| | Burns without fire-walking gear | yes/no | `TileID.Sets.TouchDamageHot` | all |
| | Trap or explosive | yes/no | what the Dangersense potion highlights (`TileDrawing.IsTileDangerous`) | all |
| Floor | Slippery | yes/no (ice, frozen slime) | `TileID.Sets.IceSkateSlippery`, type 197 | all |
| | Sticky | yes/no (honey block, cobweb) | `Collision.StickyTiles` | all |
| | Bouncy, conveyor direction, no fall damage | small codes | `Main.tileBouncy`, `TileID.Sets.ConveyorDirection`, `NegatesFallDamage` | all |
| | Breaks under the player or falls when unsupported | yes/no (thin ice, cracked bricks, sand) | `CheckIceBreak`, `CrackedBricks`, `TileID.Sets.Falling` | all |
| Background | Wall present | yes/no | `Tile.WallType != 0` | building, safety |
| Mining | Tool | pickaxe, axe, hammer | `Main.tileAxe`, `Main.tileHammer` | mining |
| | Breakable with the bot's current pickaxe, and hits needed | 0–1 | the minimum-power and damage rules in `Player.GetPickaxeDamage` (`Player.cs:55008`) | mining |
| | Value | 0–1 (ore finder priority: copper 200 … chlorophyte 700) | `Main.tileOreFinderPriority`, `TileID.Sets.Ore` | mining |
| Objects | Interactable | chest, crafting station, bed, sign | `Main.tileContainer`, `TileID.Sets.InteractibleByNPCs`, `CanBeSleptIn` | macros, building |
| | Tree | yes/no | `TileID.Sets.IsATreeTrunk` | mining (wood) |
| Visibility | Seen now / seen before | see "Fair information" | | all |

Navigate needs only the **collision, liquid, hazard and floor** groups. The rest stays empty until a skill needs it.

### Size and cost

As one byte per channel, 20 channels × 2,200 cells is 44 KB per bot per decision. At 35,000 decisions per second that is 1.5 GB/s, too much. But most channels are yes/no or small categories, so each cell fits in **4 bytes**:

1. **Class:** a tile-class id from the lookup table, which already covers solid/platform/rope/door/hazard/floor/tool/value/interactable.
2. **Shape:** the shape code.
3. **Liquid:** liquid amount and type.
4. **Flags:** wall, visibility, and per-bot values such as "breakable with my pickaxe".

That is 8.8 KB per bot per decision, about 300 MB/s for 35,000 decisions/s, which local TCP or shared memory handles. On the GPU, the learner turns the class id into its channels with a lookup table, or with a learned *embedding*: a small trainable vector per class, the same trick language models use for words.

The **mod-side cost** has to be measured. The fine grid reads 1,681 tiles and the coarse grid 8,160 per bot per decision. Each read is a pointer access (`Tilemap` is column-major, so the inner loop should run over y), but 10,000 reads plus a table lookup each could add tens of µs per bot per decision. The benchmark's `observe` option will measure the real cost. The coarse grid can also be updated less often (every 2–4 decisions), since distant terrain changes slowly.

## 2. Self state

These change every tick and are what the policy needs to predict its next movement (from `Player.Update`, `Player.cs:23066`, and the movement functions it calls):

| Group | Field | Encoding | Why |
| --- | --- | --- | --- |
| Motion | Velocity x, y | ÷10, clipped | px per tick; terminal fall speed is 10 |
| | Facing | ±1 | |
| | Position inside its tile, feet | x and y ÷16 | Decides step-ups and gap fits; the hitbox is 20×42 px |
| | On the ground | yes/no | The game's own test is `velocity.Y == 0` (`Player.cs:19572`, used everywhere) |
| | Gravity direction | ±1 | Flips every vertical meaning (gravitation potion, Brain of Cthulhu) |
| Jump | Jump ticks left | ÷ jump duration | Holding jump keeps the upward speed for `jump` ticks; releasing ends it. Main hidden state of a jump (`Player.cs:19550`) |
| | Can start a jump now | yes/no | Needs ground, a wall slide or an extra jump, and that jump was released since the last one (`releaseJump`) |
| | Extra jumps available | count, and which kind | Bottles, balloons and others differ in duration and speed |
| Air movement | Wing time left | ÷ maximum | |
| | Rocket time left | ÷ maximum | |
| | Carpet time left | ÷ 300 ticks | |
| Wall and rope | Wall sliding, and which side | yes/no, ±1 | Wall slides count as ground for jumping |
| | On a rope | yes/no | |
| | Hooks latched | count | Read after projectiles update: the player update clears the field (`Player.cs:27079`) |
| Dash | Dash cooldown or active | signed, ÷30 | A dash is a double-tap within 15 ticks; there is no dash key (`Player.cs:20120`) |
| | Double-tap window | ±15 ticks | |
| Fall | Tiles fallen so far against the safe distance | margin, normalised | Damage starts above 25 + `extraFall` tiles: 10 per tile, minus defense (`Player.cs:24330`); none with wings or a horseshoe |
| Liquid | In water, lava, honey, shimmer | 4 × yes/no | Liquids scale displacement (water ×0.5, honey ×0.25, shimmer ×0.375) |
| | Breath | ÷ maximum | Drowning starts at 0 |
| | Lava immunity left | ÷ maximum | Lava Charm budget |
| Floor | On ice, honey, asphalt | flags | Change acceleration and friction (`Player.cs:25432-25482`) |
| Survival | Life | ÷ maximum | |
| | Invincibility frames | ÷80 | After a hit (`immuneTime`) |
| | Crowd control: frozen, webbed, stoned; confused; tongued (Wall of Flesh) | flags | Controls stop working or swap |
| History | Previous action | as sent | Jumping requires release-then-press, and dashing is a double tap, so the last action is part of the state |

## 3. Capabilities

Gear and buffs change how the bot moves. The game recomputes these every tick from defaults (`Player.cs:23347-23354`), then applies equipment, buffs, floor and liquid. A human knows their gear (the tooltips list it), so this is fair.

| Field | Default | Changed by |
| --- | --- | --- |
| Top running speed | 3 px/tick | speed buffs, debuffs, floor, carpet |
| Sprint cap (with boots) | = top speed; 6 or 6.75 with boots | Hermes boots and upgrades, wings |
| Acceleration / friction | 0.08 / 0.2 px/tick² | ice, honey, wings, gear |
| Jump duration / jump speed | 15 ticks / 5.01 px/tick (apex about 6 tiles) | balloons (20 ticks, 6.51: about 11 tiles), Frog Leg (+2.4), water |
| Gravity / maximum fall speed | 0.4 / 10 | liquids, featherfall, height in the world |
| Wing flight time | 0 | 25–180 ticks per wing type |
| Rocket boots | none | 7 thrusts per landing |
| Dash type | none | Tabi, Shield of Cthulhu, armor sets |
| Wall slide gear, auto-jump, carpet | off | Climbing Claws, Frog Leg, Flying Carpet |
| Safe fall distance | 25 tiles | Lucky Horseshoe, wings: no limit |
| Water walking, lava immunity time, breath per tick, gills, featherfall | off | potions and accessories |
| Defense, maximum life, knockback immunity | 0, 100, off | armor, accessories |
| Hitbox height | 42 px | mounts |

Two traps found in the code:

- **Jump duration and jump speed are shared fields.** They are static: one value for all players, overwritten during each player's update (`Player.cs:2878, 2883`). With many bots, reading them after the tick gives the last bot's values. The mod must capture them per bot during that bot's own update.
- **Gravity is overwritten partway through a tick** under featherfall (`Player.cs:25839`). It must be read before movement, or computed.

## 4. Entities

For combat skills, and as danger awareness for Navigate:

- **What:** up to the 16 nearest NPCs, projectiles and dropped items in the view.
- **Per entity:**
  - position relative to the bot, and velocity
  - size
  - life fraction
  - contact damage
  - flags: hostile, boss, town NPC, hostile projectile
  - a type id, which the network can embed like the tile classes
- **Order:** by distance, padded with a "slot empty" flag.
- **Fairness:** in the game, enemies in the dark are drawn darkened and are effectively invisible unless they glow (`Main.cs:23255`). The same visibility rule as for tiles applies. One exception: hovering the mouse over an enemy shows its name and life even in total darkness (`Main.cs:56176`).

**Recommendation:** define the format now, but leave it empty for Navigate scenarios without enemies. The network ignores empty slots.

## 5. Goal

Set by the skill. For Navigate, the target position relative to the bot:

- **Direction:** unit vector.
- **Distance:** on a log scale.
- **Offset:** clipped to the fine grid, so the target's position in the grid is exact when it is close.

Optionally, a hint from the path planner the mod already has (`PathDistanceField`): the direction of the next waypoint. With it, the skill becomes "follow the path locally"; without it, the agent must also learn route finding.

**Open question:** should Navigate learn routes, or only local movement, with routes left to the planner? The architecture plan's controller already plans with the knowledge graph and world memory, so local movement plus a planner hint is a defensible split.

## Fair information and the server problem

What a player can actually see (`Lighting.cs`, `Graphics/Light/*`, `Map/*`):

- **Light:** tiles are drawn only if some light reaches them.
  - **Sources:** below the surface, the only light comes from torches, glowing tiles, lava and the like. Above it, from the sky.
  - **Decay:** light falls by ×0.56 per solid tile and ×0.91 per air tile, and stops at 0.0185.
  - **Effect:** a player can't see a cave behind about 7 tiles of stone. Unlit tiles are pure black.
- **The map:** the game remembers every tile that was ever lit inside the view (`Main.Map[x, y].Light > 0`). That memory is fair: the player has seen those tiles.
- **Info accessories leak beyond light.** The metal detector names the most valuable ore in a 169×124-tile area, lit or not. It is fair only when the bot wears one.

**The problem: lighting does not exist on a server.** The game computes light only in the client's drawing code ("Lighting is not calculated on the server", `Lighting.cs:12`). The map is client-only too. All throughput numbers are from headless servers. The options:

| Option | Fair? | Cost |
| --- | --- | --- |
| a. Show every tile in the view, no darkness | No: sees through rock and in the dark | Free |
| b. Run the game's light code ourselves on the server, per bot | Yes | High: the game lights about 180×130 tiles per frame for one player; 64 bots would each need their own area. Also unverified that it runs headless (sky light depends on a value set in client drawing code) |
| c. Approximate: show only exposed tiles (air, liquid and block faces next to them) within the view; underground, only within a light radius around the bot or near known light sources | Mostly: no seeing through rock or in the dark beyond the radius | Low: a neighbour check per cell |
| d. Memory: also show tiles seen in the last N seconds (or ever), marked as remembered | Yes, like the map | Low with a per-bot "seen" bitmap |

**Recommendation:** start Navigate with **c**, with the light radius as a setting. A bot is lit as if carrying a torch, which human players do underground. Add **d** when a skill needs memory beyond the view. Keep **b** as a later check of how close c is to real lighting, measured on a client where real lighting exists. Option **a** stays available as a switch for debugging and as an ablation ("how much does fairness cost?").

## Proposed v1 for Navigate

| Part | Content |
| --- | --- |
| Fine grid | 41×41, 4 bytes per cell: class (collision, liquid, hazard and floor groups), shape, liquid, flags (wall, visible now) |
| Coarse grid | 30×17 summaries of the 120×68 view: fraction solid, platform present, liquid present, hazard present, fraction visible |
| Self state | all of section 2 |
| Capabilities | all of section 3 (mostly constant per scenario for Navigate; varies once loadouts are randomised) |
| Entities | format defined, slots empty unless the scenario has enemies |
| Goal | target offset, direction, log distance; planner hint as a switch |
| Fairness | option c: exposed tiles plus light radius; option a as a switch |
| Update rate | every decision (4 ticks); the coarse grid possibly every 2–4 decisions |

Before training, three measurements:

1. **Mod time** per bot per decision, with the benchmark's `observe` option on the new observation.
2. **Bytes per second** on the wire.
3. **Learner update speed** with the new encoder, using `bench_update_ceiling.py` with the real input shapes.

## Open questions for discussion

1. Grid: two resolutions (41×41 plus a coarse 120×68 overview), or something else?
2. Navigate: learn routes, or local movement with a planner hint?
3. Fairness: is the "exposed tiles plus light radius" approximation acceptable for Navigate, with real lighting checked later on a client?
4. Entities in Navigate from the start (enemies as moving obstacles), or only once Combat starts?
5. Should capabilities be given as numbers, or should the bot infer them from its own recent movement? The second is closer to how RMA (Kumar et al. 2021) adapts to unknown dynamics, but harder to learn.
