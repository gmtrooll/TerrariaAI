# Physics Planner Design

A planner that knows how the player really moves: jump arcs, run-ups, head room, double jumps. It exists because the grid planner (`PathDistanceField`) moves one tile at a time. It cannot see that a ledge needs a 2-tile run-up, or that a low ceiling kills a jump, so the reward it gives punishes correct run-ups (Navigate Progress, Oct 19).

## Decision: option C, training signal only

Decided Oct 19. The physics planner is used **only for training**:

| Use | Planner |
| --- | --- |
| Which scenarios are reachable (generation) | physics |
| Episode time limit | physics (the real travel time of the best route) |
| Progress reward (route distance to the target) | physics |
| Waypoints in the observation (what the bot sees) | **grid, unchanged** |

The RL model has to discover run-ups and jump angles itself at test time, and no physics precomputation is needed when playing on a new world. The code stays modular, so that option B (physics waypoints in the observation as well) can be evaluated later as an upper-bound ablation. The route source is one interface with two implementations.

## Parts

1. **Movement simulator** (`Common/Planning/MovementSim.cs`). It steps a 20 × 42 px body through the world tick by tick for a given input sequence, using:
   - the game's movement order and constants: run acceleration 0.08, run slowdown 0.2, max run speed 3, jump speed 5.01 held up to 15 ticks, gravity 0.4, max fall 10, the double jump from the equipped bottle;
   - the game's own collision functions (`Collision.StepUp`, `Collision.SlopeCollision`, `Collision.TileCollision`).

   The constants come from a real player of the given loadout, read after the game computed them each tick (as the observation does). That covers liquids, webs and space gravity too.
   - Validated against real bots: jump apex (6.13 tiles single, 13.12 with Tsunami, measured earlier), horizontal jump distance, and trajectories for the same input sequences.
   - Why not the real `Player.Update` on a hidden player: about 25 µs per tick, so a world's jumps would take hours. The simulator should be one to two orders of magnitude faster.

2. **Movement graph** (`Common/Planning/MoveGraph.cs`), one per world and loadout, cached to disk.
   - **Nodes:** standing spots (feet cell plus floor, the body fits).
   - **Edges:** the result of simulating a small set of input patterns from each node, recording the first standing spot reached and its time in ticks:
     - walk left or right one tile (including falling off a ledge);
     - jump straight, or with an approach: run 0–4 tiles toward the jump side first, or back up 1–3 tiles and come forward;
     - hold the jump for 4, 8 or 15 ticks;
     - with or without the double jump at the apex;
     - drop through a platform.
   - Edges are kept only if the landing is safe: no fall damage above the limit, no lethal tiles.

3. **Route distance** (`PhysicsDistanceField`), the same role as `PathDistanceField`: the cheapest time to the target per node, by backward Dijkstra over the graph.
   - Standing bots look up their node.
   - In the air, the reward is held until the bot lands (progress is measured between standing states), so a backward run-up step is no longer punished once the jump it sets up succeeds.

## Integration

- **Scenario generation:** a start counts only if its physics distance is finite. Time limits come from the physics time plus slack.
- **Environment:** reward progress from `PhysicsDistanceField`. Waypoints and the near point still come from `PathDistanceField` (option C); a switch selects physics waypoints for option B.
- **Cost:** graph building per world (estimated minutes), cached; distance fields per target as today.

## Validation plan

1. Simulator against measured jumps and real bot trajectories (same inputs, compare positions tick by tick).
2. Graph on one world rendered: nodes and jump edges, and routes for the failure cases (run-up from the screenshot, low ceiling).
3. Compare reachability with the grid planner on the existing scenario sets: how many grid-reachable scenarios are physically impossible, and the reverse.
4. Train with the physics reward (option C) and compare with `best_30m` on the 1,024 set, including the run-up cases.
