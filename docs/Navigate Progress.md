# Navigate Progress

Work after the overnight runs (`docs/Overnight Log.md`), newest last.

## Oct 18: world integrity, hazards, planner costs

Your decisions:
- keep Tsunami in a Bottle for method experiments, and train on mixed equipment once the methods are settled;
- stride 2 as the default;
- trap projectiles (rolling cactus included) stay out of Navigate (no entities);
- handle every hazard that matters if it is cheap (`docs/Movement Hazards.md`).

My call on respawning: death ends the episode, and the next one starts at its scenario's start, as before. Respawning on the spot would make dying near the goal cheap.

What changed:

| Change | Why |
| --- | --- |
| No world saving in environment mode (`WorldIntegrity`) | Every run had been writing bot changes (broken webs, open doors, used traps) into the world files; `eval_01` too, during watching |
| Tile snapshot at connect; changed tiles near where bots were are put back once no bot is within 4 tiles. No item drops; hostile projectiles cleared | One mechanism for cobwebs, thorns, thin ice, cracked bricks, doors, traps and sand. Tested: a deliberately broken tile came back, and 20 tiles the bots changed were restored in one test run |
| Planner: cheapest path with costs (water 2, honey 4, cobweb 8) | Cobwebs cap speed at about 1 tile per 30 ticks; time limits and the progress reward now follow the real crossing time |
| Planner: blocked cells (body touching spikes, thorns or hot blocks; lava, shimmer; within 2 tiles of traps and pressure plates) and no hard falls over 25 tiles unless landing in liquid or on clouds | Removes routes the game makes deadly or that set off traps |
| Planner: closed doors count as open (bots open them as the local player); starts and targets not in doorways | Doors looked like walls |
| All 8 worlds recreated from their seeds, all scenario sets regenerated | The old files had been modified |
| Episode recorder (`evaluate --record`) and `navigate.failures` (pictures of failed episodes with the planned route) | To check whether failures are the bot's or the planner's |
| Stride 2 is the default in `train.py` | Overnight result |

Cost: building the planner's distance field takes about 1 ms per decision (fields are cached per target); the server still runs at about 2,450 steps/s with 32 bots.

## The same model on the new scenario sets

`stride2` (trained on the old sets, 5M steps, 30 minutes), held-out worlds, new lists:

| | Overall | 5–15 | 15–30 | 30–60 | 60–120 | Surface | Underground | Cavern |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Old sets | 79% | 90% | 84% | 77% | 66% | – | – | 71% |
| **New sets** | **85%** | 93% | 91% | 79% | 77% | 92% | 82% | 79% |

The gain comes from the scenarios, not the model: impossible and unfair routes are gone.

## What the remaining failures are

40 of the 97 recorded failures were rendered (`results/training/stride2/failures/`; magenta = planned route, cyan = the bot's path, green = start, red = target), and 16 were inspected:

| Kind | Count | Example | Notes |
| --- | --- | --- | --- |
| Long gap jump (8–10 tiles), bot falls in | 4 | `eval_01_12`, `eval_02_03` | Possible by the game's numbers: with Tsunami, a double jump covers about 13 tiles from a standing start, 17 with a run-up. The bot mistimes the double jump |
| Approach angle: jumps straight up under an overhang instead of going up beside it | 3–4 | `eval_02_06`, `eval_01_05` | What you saw while watching |
| Detour: the route first turns away from the target (around a corner, into a pocket) | 3 | `eval_01_13`, `eval_01_07` | The bot stays near the start |
| Too slow: follows a long route correctly but runs out of time | 1 | `eval_02_01` | 118-tile route through a cabin |
| Route questionable (planner may be optimistic) | 2 | `eval_01_10`, `eval_01_00` | E.g. a 10-tile climb from a small outcrop above a pit |

37 of 40 failures are timeouts: the bot repeats the same failed attempt until time runs out.

**Conclusion:** after today's planner fixes, most failures are the policy's, on routes that are possible. Next:
- more training (a 50M-step run, `long_v3`, started);
- then memory: a recurrent policy that knows it just failed and tries something else;
- and a finer decision interval for jump timing.

The physics-based planner is not urgent.

## Oct 19 night: longer training and memory

All runs use the new worlds and scenario sets, custom PPO, stride 2, bf16 + overlap. Held-out = `eval_01` and `eval_02`, 512 fixed scenarios, sampled actions.

| Run | What | Held-out | 5–15 | 15–30 | 30–60 | 60–120 | Cavern | Timeouts | Training win (end) | Steps/s | Wall-clock |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `stride2` (before) | 5M steps, trained on the old sets | 85% | 93% | 91% | 79% | 77% | 79% | – | – | 2,850 | 30 min |
| `base_10m` | 10M steps | 88% | 96% | 90% | 86% | 81% | 82% | 11.1% | 91% | 4,570 | 37 min |
| `rec_10m` | 10M steps, **memory (GRU)** | **91%** | 92% | 94% | 88% | **89%** | 84% | 8.6% | 91% | 3,250 | 51 min |
| `fs2_20m` | 20M decisions **every 2 ticks** (same game time as 10M at 4 ticks), γ 0.995 | 87% | 95% | 89% | 82% | 80% | 78% | 11.7% | 92% | 4,815 | 70 min |
| `long_v3` | **50M steps** | **92%** | 98% | 92% | 89% | 88% | 84% | 7.8% | 97% | 4,690 | 3 h |

Curves: [memory.png](../results/training/memory.png).

Findings:
- **Longer training helps a lot, then levels off.** Training success rises from 75% at 5M to 91% at 10M and about 97% from 25M on; the last 25M steps add little. Held-out 92%: a gap of about 5 points to the training worlds is opening (more training worlds would close it).
- **Memory helps on unseen worlds at equal training:** 91% vs 88% overall, and 89% vs 81% on the longest routes, with fewer timeouts (8.6% vs 11.1%). The training curves are the same, so memory doesn't speed learning on known worlds; it generalizes better. With 10M steps it nearly matches the 50M-step run without memory. One seed each, so the overall +3 points is suggestive; the +8 on long routes is the clearer signal. Cost: 29% slower per step (the update replays sequences).
- **Deciding every 2 ticks instead of 4 does not help** (87% vs 88% at the same game time) and takes twice the wall-clock time. The mistimed jumps are not about reaction time. Decisions stay at every 4 ticks.
- The PC ran faster tonight (4,500–4,700 steps/s against 2,850 before) with the other programs closed.

### What the 50M-step model still fails

24 of its 62 recorded failures were rendered (`results/training/long_v3/failures/`). Two clear patterns:
- **Detours hidden by the waypoint distance.** Example `eval_02_01` (route of 7): the bot starts on a small ledge next to a wall; the route steps 1 tile right, drops 5, and goes 1 tile left under the ledge to the target. The bot stands at the ledge edge pushing down-left into the wall for the whole episode. The waypoint is 12 route tiles ahead, already past the turn, so it points down-left too: the first move (right) is invisible to the policy. This matches your observation that small detours work and bigger ones don't. **Next experiment: a near waypoint (3 tiles ahead) next to the far one**, so the immediate next move is always visible. Cheap: 5 more numbers in the goal input.
- **Flat jumps under a low ceiling** (`eval_02_05`): the route crosses a 5-tile gap from a ledge with a ceiling just above. The planner lets the body move sideways through the air there, but with no room to jump the bot only falls. This is planner optimism (it doesn't model head room for jumps); it matters only in a few scenarios.
- Also still seen: tight pockets (`eval_01_09`, failed by both models), overhang approach angles (`eval_01_11`).
