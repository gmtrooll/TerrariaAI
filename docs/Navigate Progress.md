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
| `near_10m` | 10M steps, **near waypoint** (3 route tiles ahead) next to the 12-tile one | 88% | 95% | 89% | 85% | 84% | 83% | 10.0% | 94% | 4,513 | 39 min |
| `near_rec_10m` | 10M steps, **near waypoint + memory** | **93%** | 97% | 94% | 90% | 90% | **88%** | 6.8% | 93% | 3,267 | 51 min |
| `best_30m` | 30M steps, near waypoint + memory | **93%** | 100% | 94% | 91% | 88% | 89% | 6.1% | 96% | 3,285 | 2 h 34 min |
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


## Oct 19 morning: near waypoint, evaluation noise, the hardest scenarios

**Near waypoint: no held-out gain** (88.5% vs 88.3% for `base_10m`), although it learns the training worlds faster (94% vs 91%) and is 3 points better on the longest routes. The detour case that motivated it (`eval_02_01`) still fails sometimes: the near point mostly says "down", while the bot needs to shuffle about one more tile to the right before its 20 px body clears the ledge. That is a sub-tile body-position detail neither waypoint conveys.

**Evaluation noise is large.** The 512-scenario held-out set gives one standard error of about ±1.4 points. Failures are partly luck: `base_10m` and `near_10m` share 41 failed scenarios, and 27 and 29 differ. Differences under about 3 points between single runs should not decide anything. Next decisions will use a doubled evaluation set and, where it matters, a second seed.

**18 scenarios fail for every model** (`base_10m`, `rec_10m`, `near_10m`, `long_v3`); rendered in `results/training/common_failures/`. Of 8 inspected:

| Kind | Count | Examples |
| --- | --- | --- |
| Underground cabins and structures: platform floors and roofs, wooden walls, doors | 4 | `eval_01_04`, `eval_02_00`, `eval_02_04`, `eval_02_06` |
| Tight passages: a narrow 8-tile vertical shaft, a pit with a tight exit | 2 | `eval_02_07`, `eval_01_06` |
| Hill lip, very long route | 2 | `eval_01_01`, `eval_01_03` |

The cabin cluster is the clearest lead. Routes there drop through platforms (only possible by holding down while standing on one) or go through doors (they open only when walked into at floor level). Both are rare in training scenarios. Two possible fixes:
- sample hard scenarios more often (Prioritized Level Replay: replay scenarios with low success);
- add scenarios that must pass a platform or a door.


**Near waypoint + memory together: 92.6% held-out at 10M steps**, the best result so far, above the 50M-step run without them (91.8%) in a fifth of the training. Caverns 88% (the hardest layer; 82–84% for every other run), fewest timeouts (6.8%). Neither addition alone did this well (memory 90.6%, near 88.5%): the two seem to work together. The memory may let the policy use the near point's short-term direction (go right first, then down) over several decisions. Still one seed: the next decisive comparison uses the 1,024-scenario evaluation. `best_30m` (near + memory, 30M steps) started automatically.


## Oct 19 midday: the best model so far

`best_30m` = near waypoint + memory, 30M steps (2.5 hours). Larger held-out evaluation (1,024 scenarios, 128 per band per world; ± is one standard error):

| Model | Held-out | 5–15 | 15–30 | 30–60 | 60–120 | Surface | Underground | Cavern |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **`best_30m`** | **94.4% ± 0.7** | 99% | 94% | 95% | 89% | 98% | 92% | 93% |
| `long_v3` (50M, neither) | 92.9% ± 0.8 | 98% | 93% | 94% | 86% | 97% | 90% | 91% |

- `best_30m` is the best model: +1.5 points with 40% less training, largest on long routes (+3) and in caverns.
- On the 512 set it scores 93.2%, barely above `near_rec_10m` (92.6% at 10M). Its training success is 96%, so more steps on the same 6 worlds now mostly improve the training worlds. **More training worlds** is the next lever for unseen terrain.
- Watch it: `python -m navigate.watch --run best_30m` (from `Training`).

## Oct 19 afternoon: prioritized replay, and the larger evaluation

1,024-scenario held-out set (± one standard error):

| Model | Held-out | 5–15 | 15–30 | 30–60 | 60–120 | Cavern | Timeouts |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `near_rec_10m` (near + memory, 10M) | 91.5% ± 0.9 | 99% | 92% | 91% | 85% | 89% | 8.0% |
| `replay_10m` (near + memory + **replay of hard scenarios**, 10M) | 91.6% ± 0.9 | 97% | 92% | 92% | 86% | 89% | 7.9% |
| `best_30m` (near + memory, **30M**) | **94.4% ± 0.7** | 99% | 94% | 95% | 89% | 93% | 5.2% |

- **Prioritized replay brings no gain** (91.6% vs 91.5%). Half the episodes went to the scenarios the bot was failing, but the held-out result is unchanged. The hard cases (cabins, tight passages) seem to need something other than repetition, e.g. scenarios that teach dropping through platforms and passing doors.
- **The larger evaluation corrects the 512-set number for `near_rec_10m`** (91.5% instead of 92.6%): the smaller set was lucky by about one standard error. Decisions from here on use the 1,024 set.
- With near + memory, 30M steps instead of 10M gives +3 points held-out.

### Robustness fixes found on the way

- **Orphan servers:** an evaluation whose Python process died left its two servers running, holding memory and the mod file (which blocked installing a new build). Training servers now exit when their learner disconnects.
- **One faulty bot stopped a whole server:** in `worlds12_10m` a bot on `train_10` ended up outside the world; its update then threw every tick, which aborted the rest of the tick (including the environment's decision step), so the learner waited forever. Bot updates now catch exceptions per bot, and a faulted or out-of-world bot's episode ends as a death and is logged (position, velocity, scenario) to find the cause.
- **Duplicate runs:** stopping a queue script did not always stop its shell, so a stale copy started a second `replay_10m` on the same port. Both were stopped, and `replay_10m` was rerun once.
- **12 worlds fit in memory** (each server uses about 0.93 GB). Launching 12 servers at once takes a while; training then runs at the usual speed (3,200 steps/s with memory).

## Oct 19 afternoon: 12 training worlds

`worlds12_10m` = near waypoint + memory, 12 training worlds × 16 bots (same 192 bots as 6 × 32), 10M steps. 1,024-scenario held-out set:

| Model | Held-out | 5–15 | 15–30 | 30–60 | 60–120 | Surface | Underground | Cavern | Training win (end) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `near_rec_10m` (6 worlds) | 91.5% ± 0.9 | 99% | 92% | 91% | 85% | 96% | 89% | 89% | 93% |
| `worlds12_10m` (12 worlds) | 91.4% ± 0.9 | 97% | 94% | 90% | 85% | 98% | 90% | 86% | 88% |

- **No difference at 10M steps.** With 12 worlds the training success is lower (more variety, less practice per world) but held-out is the same: at this length the model is not yet overfitting 6 worlds.
- The train/held-out gap only appears with longer training (`best_30m`: 96% training, 94% held-out), so 12 worlds may pay off in a long run. That needs a 30M-step run on 12 worlds to tell.

## Oct 19 evening: damage penalty, webs per episode, a collapsed run

Your decisions:
- Damage avoidance belongs to Navigate itself; the full hazard stage comes later together with random equipment and entities.
- Now only a small reward penalty for HP lost: −5 per full life (losing all life costs as much as dying; not counted on the decision a bot dies).
- Cobwebs a bot breaks stay broken until its episode ends (you saw them come back while a bot was still crossing).

`final12_50m` (near + memory, 12 worlds, damage penalty, seed 0) **collapsed** after the curriculum opened all bands. At 0.54M steps it won 13%, against 43% for the 12-world run without the penalty, and its policy entropy was rising. Stopped.

Diagnostic `diag_pen5`: the same new code (damage penalty, webs per episode) on 6 worlds is healthy through 1.5M steps (band win rates 93 / 80 / 69 / 60%, on the usual curve). So the new code does not break learning. The collapse was either bad luck (PPO occasionally takes a bad update) or specific to 12 worlds. `final12_50m` was restarted with seed 1. If it collapses too, the final run falls back to 6 worlds.

**Cause found: oversized policy updates.** Seed 1 collapsed in the same place. In both collapsed runs the KL per update (how far one update moves the policy) rose to 0.4–0.5 just before the collapse; healthy runs stay at 0.002–0.08. The policy got thrown off course by a few oversized updates, then wandered randomly (entropy up, timeouts up). Why the 12-world setup provokes them is not clear yet.

Fix: **target KL** (`--target-kl`, as in SB3). A minibatch whose KL exceeds 1.5 × the target is skipped, and the update's remaining epochs are cancelled. With 0.03, `final12_50m` (seed 1) stepped in once, at iteration 13, and then trained normally through 1M steps (band win rates 92 / 77 / 63 / 48%, KL 0.006–0.011 per update). It is now running to 50M steps (about 5 hours).

### The final run's checkpoints (1,024-scenario held-out set)

`final12_50m`: near waypoint + memory, 12 worlds, damage penalty, target KL 0.03, seed 1.

| Model | Held-out | 5–15 | 15–30 | 30–60 | 60–120 | Cavern | Timeouts |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `best_30m` (6 worlds, 30M) | 94.4% ± 0.7 | 99% | 94% | 95% | 89% | 93% | 5.2% |
| `final12_50m` at 10M | 94.1% ± 0.7 | 99% | 95% | 93% | 90% | 92% | 5.7% |
| `final12_50m` at **20M** | **95.6% ± 0.6** | 99% | 96% | 96% | 91% | 94% | 3.9% |

- **12 worlds pay off with longer training.** At 10M the run matches `best_30m` (at 30M), and from 10M to 20M it gains another 1.5 points held-out. At 10M steps alone (Oct 19 afternoon) 12 worlds showed no gain; the extra variety needs longer training to show.
- The 20M copy is the new best model. The run continues to 50M; the 30M and 40M copies are evaluated as they come, and the best copy becomes the final Navigate model.

**Held-out levels off at about 30M steps:** 10M 94.1%, 20M 95.6%, **30M 96.2% ± 0.6**, 39M 96.1%. The run stopped at 39.8M: saving the 40M copy failed partway (a write error, not a full disk; probably the file was briefly locked), and the last good model file was kept.

**Final Navigate model v1 = `navigate_v1`** (the 30M copy of `final12_50m`): near waypoint + memory, 12 training worlds, damage penalty, target KL, Tsunami in a Bottle. **96.2% on unseen worlds** (1,024 scenarios): 99.2% / 97.3% / 95.7% / 92.6% by band, caverns 95.5%, timeouts 3.6%. Watch it: `python -m navigate.watch --run navigate_v1`.

## Oct 20 night: the physics progress reward (option C)

Movement graphs (sampler v2) for 6 training and 2 held-out worlds: 174k–191k standing spots and 2.2–2.5M safe moves per world; **92–96% of each world's scenarios are physically reachable** (the rest keep the grid distance). About 12 minutes per world with 6 servers.

`phys_10m` = near waypoint + memory, 6 worlds, 10M steps, **progress reward from the physics route distance** (held in the air); waypoints and the win test still from the grid planner. 1,024-scenario held-out set:

| Model | Held-out | 5–15 | 15–30 | 30–60 | 60–120 | Cavern | Timeouts |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `near_rec_10m` (grid reward) | 91.5% ± 0.9 | 99% | 92% | 91% | 85% | 89% | 8.0% |
| **`phys_10m`** (physics reward) | **93.3% ± 0.8** | 98% | 93% | 93% | **88%** | **93%** | 6.3% |

- **+1.8 points overall, largest on long routes (+3.5) and in caverns (+3.8)**, where the grid reward misled the bot most, and fewer timeouts.
- **Not yet a clean comparison:** `phys_10m` also has the damage penalty, the target KL and webs-per-episode, which `near_rec_10m` did not. The control `grid_ctrl_10m` (identical settings, grid reward) runs next.

**The clean control says the physics reward itself makes no measurable difference.** `grid_ctrl_10m` has exactly `phys_10m`'s settings with the grid reward:

| Model (10M steps, 1,024 set) | Held-out | 5–15 | 15–30 | 30–60 | 60–120 | Cavern | Timeouts |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `near_rec_10m` (old settings, grid reward) | 91.5% ± 0.9 | 99% | 92% | 91% | 85% | 89% | 8.0% |
| `grid_ctrl_10m` (new settings, grid reward) | **93.4% ± 0.8** | 98% | 94% | 95% | 86% | 92% | 5.9% |
| `phys_10m` (new settings, physics reward) | 93.3% ± 0.8 | 98% | 93% | 93% | 88% | 93% | 6.3% |

- The +1.8 over `near_rec_10m` came from the target KL, the damage penalty and webs-per-episode, not from the physics reward.
- The physics reward is about 2 points better on the longest band and in caverns. With 256 scenarios per band that is within the noise, so it is not shown.
- Possible reasons:
  1. With the near waypoint and memory, the bot already copes with most routes where the grid reward was misleading.
  2. The physics graph still misses some moves (4–8% of scenarios fall back to the grid distance).
  3. The cases it targets (run-ups, approach angles) are a small share of all failures.

  A targeted test on those cases specifically (scenarios whose physics route differs most from the grid route) would separate these.

**Targeted check: no difference even where the planners disagree.** Both models re-evaluated on the 1,024 set with per-scenario outcomes (both 93.1% overall), grouped by how much longer the physics route is than the grid route (route time ÷ grid distance):

| Physics route vs grid route | Scenarios | Grid reward | Physics reward | Solved only by grid / only by physics |
| --- | --- | --- | --- | --- |
| Off the physics graph | 49 | 77.6% | 75.5% | 5 / 4 |
| Similar (< 1.3×) | 747 | 94.4% | 94.4% | 14 / 14 |
| Longer (1.3–2×) | 196 | 92.3% | 92.3% | 8 / 8 |
| Much longer (≥ 2×, where run-ups and detours are) | 32 | 90.6% | 93.8% | 1 / 2 |

**Conclusion: option C (physics route distance as the progress reward) brings no measurable gain** at 10M steps, not even on the routes it targets. The accuracy of the progress reward is not what limits the bot. A useful negative result for the thesis: the coarse grid reward is good enough when the bot has the route waypoints and memory.

What remains open:
- Option B (physics waypoints in the observation) as an upper bound: does *telling* the bot about run-ups help, when a better reward does not?
- Using the physics graph for what it measured well: scenario reachability (92–96% of scenarios physically reachable, the rest flagged).

## Oct 20 night: where the bot gets stuck at ledges

The user, watching `navigate_v1` (the final 30M model, 96.6% held-out), saw it shaking left and right on a ledge above a nearby target instead of walking to the ledge's end and dropping; the same under a ledge with the target on top.

### A planner squeeze (7 of navigate_v1's 37 failures)

7 of the 37 held-out failures end on one tile of `eval_02` (3869, 396). The probe (`navigate.probe`: action probabilities at a tile, fresh start vs arriving there) shows the bot pressing left with probability 1.00 at speed 0: its head is against an overhang and its feet on the ledge, and the opening below is 1 tile wide; the body is 1.25 tiles. The grid planner allowed it because a cell counts as passable with room on either side, and the ledge cell (room only on the right) connects to the cell below (room only on the left).

Fix: `-plannerbody` (off by default; `PathDistanceField` consistentBody). A drop must keep one clear side column, counting full blocks only. Steps up are exempt: walking into a 1-tile ledge lifts the player onto it in one tick (`Collision.StepUp`). Slopes and half blocks don't block. Checked against the bot's outcomes (`navigate.planner_check`):

| Rule | eval scenarios made unreachable | Of those in the 1,024 set: solved by navigate_v1 |
| --- | --- | --- |
| Same side, any solid tile, up and down | 60 | 20 of 31 (too strict) |
| Full blocks only | 24 | 3 of 11 (all 1-tile steps up) |
| **Full blocks only, drops only** | **12** | **0 of 8** |

### Hand-made tests (`navigate.handmade`)

Small structures in a separate world (`handmade`), one thing varied at a time, 8 tries each:
- **drop:** bot on a 1-tile-thick ledge (open only at its left end), target on the floor at column c (0 = ledge end, + = under the ledge), ledge height H;
- **climb:** the same structure, bot under the ledge, target on top;
- **pocket:** a C-shaped pocket 3 tiles tall opening toward the bot, target behind it.

`navigate_v1`:

| Test | Result |
| --- | --- |
| drop, target beyond the ledge end (c ≤ 0) | 8/8 at every height |
| drop, target under a 4- or 6-tile ledge (c ≥ 4) | 0–2 / 8 |
| drop, target under a 10-tile ledge | 8/8 |
| climb onto the ledge from under it | 0–6 / 8 (23% overall) |
| pocket | 8/8 |

The ledge height mattering, not the direction alone, is explained by the far waypoint. Probed at the ledge end (H = 6, target under the ledge): the 12-step waypoint is at (+4.5, +5) tiles, back under the ledge (12 route steps go off the edge, down and back right), and the policy follows it right (probability 0.93). With H = 10 the 12 steps stay in the drop shaft, the waypoint is at (+1.5, +9) and the bot steps off (left 0.68–0.96). Climbs fail the same way: near the ledge end the waypoint jumps onto the ledge top behind the bot.

### Line-of-sight waypoint (`los=1`)

New observation option: the far waypoint is the furthest route point, up to 12 ahead, with every route point up to it in line of sight from the bot (`Collision.CanHitLine`). `navigate_v1` with it, **no retraining**:

| | Without | With line of sight |
| --- | --- | --- |
| hand-made drops | 63% | **100%** |
| hand-made climbs | 23% | **100%** |
| hand-made pockets | 100% | 100% |
| held-out 1,024 set | 96.6% | 97.1% (9 fixed, 4 broken) |
| held-out, without the 8 squeeze scenarios | 97.3% | 97.8% |

The held-out worlds have few short drops under ledges, so the effect there is small; the hand-made cases are the situations the user saw. `los_10m` (trained with it) runs tonight.

## Oct 20 night: two equipment loadouts

Does mixed equipment need much more training? Three 10M runs with `grid_ctrl_10m`'s settings, each evaluated on both loadouts (1,024 sets; the "no extra jump" set holds the scenarios reachable with a 6-tile jump, `_j6`):

| Model (10M steps) | With Tsunami | Without extra jump | Mean |
| --- | --- | --- | --- |
| `grid_ctrl_10m` (trained with Tsunami only) | **93.1% ± 0.8** | 83.5% ± 1.2 | 88.3% |
| `none_10m` (trained without only) | 87.3% ± 1.0 | 83.3% ± 1.2 | 85.3% |
| `mix_10m` (trained 50/50) | 91.8% ± 0.9 | **85.2% ± 1.1** | **88.5%** |

- **Mixing costs little at the same budget:** `mix_10m` is within noise of the specialist on each loadout (−1.3 with Tsunami, +1.7 without), and has the best mean.
- **Training with Tsunami transfers to no extra jump** (83.5%, the same as training without it), but not the reverse: `none_10m` never learns to use the double jump (87.3% with it).
- **Without the extra jump everything is harder** (83–85%): no mid-air correction, more precise jumps. The 30–60 and 60–120 bands drop most (77–81% and 73–75%).
- For the planned random-equipment stage: train on the mixture from the start; the cost per loadout looks small, at least for two loadouts that differ in jump height.

## Oct 20 night: more bending routes in training

Success falls with how much the route bends away from the target (route length ÷ straight-line distance): `navigate_v1` 98.9% below 1.3×, 90.6% at 1.7–2.5×, 88% above; the 10M models 71–77% above 1.7×. Such scenarios are only 4–15% of training episodes (rarest in the short bands the curriculum starts with). `detour_10m` = `grid_ctrl_10m` with 30% of episodes drawn from scenarios with a ratio of at least 1.7 (`--detour 0.3`), evaluated on the 1,024 set and on all 316 held-out scenarios with that ratio (`--min-detour 1.7`):

| Model (10M steps) | 1,024 set | 316 detour scenarios |
| --- | --- | --- |
| `grid_ctrl_10m` | 93.1% ± 0.8 | 81.3% ± 2.2 |
| `detour_10m` | 93.8% ± 0.8 | **85.4% ± 2.0** |
| `navigate_v1` (30M, 12 worlds), for reference | 96.6% | 87.3% ± 1.9 |

Paired on the same scenarios: on the detour set 24 solved only by `detour_10m`, 11 only by `grid_ctrl_10m` (McNemar z = 2.2); on the 1,024 set 33 vs 25 (z = 1.05). **Oversampling bending routes helps on them (+4 points) at no cost elsewhere**; at 10M steps it closes most of the gap to the 30M model on that set.

## Oct 20 morning: training with the line-of-sight waypoint

`los_10m` = `grid_ctrl_10m` with `los=1` (all 10M steps, 6 worlds; hand-made: 8 tries per variant, planner route lengths as time limits):

| Model | 1,024 set | 60–120 band | 316 detour scenarios | hand-made drop | hand-made climb | hand-made pocket |
| --- | --- | --- | --- | --- | --- | --- |
| `grid_ctrl_10m` | 93.1% ± 0.8 | 85% | 81.3% ± 2.2 | 76% | 14% | 98% |
| `detour_10m` | 93.8% ± 0.8 | 87% | 85.4% ± 2.0 | 91% | 73% | 100% |
| **`los_10m`** | **94.9% ± 0.7** | **91%** | **87.0% ± 1.9** | **100%** | **93%** | 100% |
| `navigate_v1` (30M, 12 worlds) | 96.6% | 94% | 87.3% ± 1.9 | 64% | 21% | 96% |

Paired against `grid_ctrl_10m`: 1,024 set 42 solved only by `los_10m`, 23 only by `grid_ctrl_10m` (z = 2.4); detour set 28 vs 10 (z = 2.9). **The line-of-sight waypoint is the largest single gain since the near waypoint**: the long routes gain most, and at 10M steps it matches the 30M model on bending routes while solving the ledge cases that model fails. Oversampling bending routes also helps, less (and was not combined yet).

Next: `losdetour12_30m`, `navigate_v1`'s setup (12 worlds, 30M steps) with both changes, as the candidate for `navigate_v2`.

## Oct 20 morning: navigate_v2

`losdetour12_30m` = `navigate_v1`'s setup (12 worlds, 16 bots each, seed 1, 30M steps) with the line-of-sight waypoint and 30% bending routes:

| | `navigate_v1` (30M) | **`navigate_v2`** (30M) |
| --- | --- | --- |
| 1,024 set | 96.6% | 96.6% ± 0.6 (paired: 15 solved only by each) |
| … without the 8 planner-squeeze scenarios | 97.3% | 97.3% |
| 316 detour scenarios | 87.3% | **90.2%** (paired 19 vs 10, z = 1.7) |
| hand-made drop / climb / pocket | 64% / 21% / 96% | **100% / 100% / 100%** |
| at 10M steps (1,024 set) | 94.1% | 96.4% |

- The 1,024 set looks saturated around 96.5–97%: the two models tie there; the gains are where the old waypoint misled (ledges) and on bending routes.
- v2 reaches v1's final score by 10M steps.
- Evaluation noise check: two evaluations of the same 10M checkpoint gave 95.4% and 96.4% (sampled actions). Single-evaluation differences of about a point are noise; paired comparisons on the same scenarios are the reliable test.

`results/training/navigate_v2/` holds the model, config and evaluations. Watch it with `python -m navigate.watch --run navigate_v2`.

### v1 vs v2 with repeated evaluations

Why a tie on the 1,024 set but a gain on bending routes? Bending routes (≥ 1.7×) are only 134 of the 1,024 scenarios (13%), and on those 134, two evaluations of the same model differ by about 2 points (v1: 89.6% then 87.3%, 11 outcomes flipped). Both models were evaluated 5 times on both sets:

| | `navigate_v1` (5 runs) | `navigate_v2` (5 runs) | v2 − v1, paired per scenario |
| --- | --- | --- | --- |
| 1,024 set | 96.56% (runs 96.2–97.0) | 96.80% (96.4–97.1) | +0.23 ± 0.31 (z = 0.8) |
| 316 detour scenarios | 87.91% (86.7–88.6) | 89.94% (88.6–90.5) | +2.0 ± 1.2 (z = 1.75) |

- The 1,024 set is a tie. A +2 gain on 13% of it adds about 0.3 points overall.
- On bending routes v2 is ahead in all 5 runs (better on 38 scenarios on average, worse on 20), but z < 2: the remaining uncertainty comes from the few hard scenarios, not from evaluation runs (run-to-run sd 0.8). A larger bending-route set would settle it.
- Run-to-run sd on the 1,024 set is about 0.3 points; the binomial ± 0.6 overstates the noise of comparing two models on the same fixed scenarios.
- The clear difference is the ledge situations (hand-made: 100% vs 64% / 21%), which are rare in the generated scenarios.

### A larger bending-route test set

`navigate.bend_set` samples new scenarios from the two held-out worlds with fresh seeds (the worlds are not regenerated) and keeps routes at least 1.7× the straight line, leaving out the main set's scenarios: 1,136 scenarios (`eval_01_bend`, `eval_02_bend`; 150 per band, fewer in 5–15 where bending routes are rare). Evaluated 3 times per model (`evaluate --set _bend`):

| | runs | mean |
| --- | --- | --- |
| `navigate_v1` | 93.2 / 92.9 / 92.6 | 92.9% |
| `navigate_v2` | 95.2 / 95.1 / 95.3 | **95.2%** |

Paired per scenario: **+2.3 ± 0.6 points (z = 4.1)**, v2 better on 76 scenarios, worse on 36. With enough bending scenarios the gain is clear; the 316-scenario set was too small to show it.

### More pocket variants

The user's C-shape (bot inside a small pocket, has to back out to jump over) reproduced: bot starting inside a pocket with a 5-tile lip: `navigate_v1` 1/8, `navigate_v2` 8/8. With a 3-tile lip, or a back wall 2 tiles taller than the lip, both 8/8.

### What navigate_v2 still fails

One recorded evaluation of the 1,024 set (`eval_big_rec`, 43 failures counting repeats; pictures in `results/training/navigate_v2/failures/`):

| Kind | Failures | What happens |
| --- | --- | --- |
| Planner squeeze (unreachable with `-plannerbody`) | 10 | the `eval_02` overhang drop (8), one unreachable start (2) |
| Stuck off the route / wandering | 16 | dropped into a pit or side cave off the route and never got back; complex cave topology |
| Died | 7 (5 scenarios) | long jumps over lava to small platforms; long falls without using the double jump to break them |
| Ledges and gaps | 10 | climbs of 5+ tiles, drops off edges, gaps |

- With `-plannerbody` the squeeze cases leave the set (they are not solvable).
- The deaths are the planned hazard stage: damage avoidance and breaking long falls with the double jump (which the user noticed the bot rarely does).
- Getting back after leaving the route (pits, side caves) is the biggest remaining policy weakness.

## Oct 20 day: ablation and second seeds (10M steps, 6 worlds)

| Model | Line of sight | 30% bending routes | Seed | 1,024 set | 316 detour scenarios | hand-made drop / climb / pocket |
| --- | --- | --- | --- | --- | --- | --- |
| `grid_ctrl_10m` | | | 0 | 93.1% | 81.3% | 76 / 14 / 98 |
| `grid_ctrl_10m_s2` | | | 2 | 93.5% | 83.5% | 75 / 33 / 74 |
| `detour_10m` | | ✓ | 0 | 93.8% | 85.4% | 91 / 73 / 100 |
| `los_10m` | ✓ | | 0 | 94.9% | 87.0% | 100 / 92 / 100 |
| `los_10m_s2` | ✓ | | 2 | 94.7% | 86.7% | 100 / 97 / 100 |
| `losdetour_10m` | ✓ | ✓ | 0 | **96.6%** | 86.1% | 100 / 95 / 100 |

- **The line-of-sight gain replicates across training seeds.** Averaged over both seeds, paired per scenario: +1.6 ± 0.6 points on the 1,024 set (z = 2.8), +4.4 ± 1.4 on the detour set (z = 3.2). The hand-made ledge behaviours are stable with it (climbs 92–97%) and fragile without (climbs 14–33%, pockets 74–98% between seeds).
- **Both changes together** reach 96.6% at 10M steps on 6 worlds, `navigate_v1`'s 30M / 12-world score; paired against `los_10m`: 29 scenarios solved only with both, 12 only with line of sight alone (z = 2.7). On the small detour set the two line-of-sight runs are within noise (13 vs 10); the bending-route set evaluation follows.

**On the 1,136-scenario bending-route set** (2–3 evaluation runs each):

| Model | Bending-route success |
| --- | --- |
| `grid_ctrl_10m` / `_s2` | 88.7% / 89.3% |
| `detour_10m` | 90.8% |
| `los_10m` / `_s2` | 92.6% / 93.4% |
| `losdetour_10m` | 92.9% |
| `navigate_v1` (30M) | 92.9% |
| `navigate_v2` (30M) | **95.2%** |

Paired per scenario: line of sight vs control over both seeds **+4.0 ± 0.6 (z = 7.3)**; bending routes alone vs control +2.1 ± 0.7 (z = 3.0); adding bending routes on top of line of sight +0.3 ± 0.5 (no effect here, though +1.7 on the 1,024 set, z = 2.7). Once the waypoint is right, extra practice on bending routes no longer helps on them.

Also: `navigate_v2`, trained with Tsunami only, gets 86.7% without the extra jump (`mix_10m` 85.2%, `grid_ctrl_10m` 83.5%).

## Oct 20 afternoon: a "blocked" input does not help

Snags (pressing left or right while barely moving, `evaluate` now saves the longest run per episode): in one evaluation of `navigate_v2`, 12 of 29 failures pressed while blocked for 40+ decisions (the planner squeeze, slope snags such as `eval_02` (1262, 351): left 1.00 at speed 0, jump 0.00). `losblocked_10m` = `los_10m` + `blocked=1` (decisions in a row pressing without moving, ÷ 20):

| | `los_10m` | `losblocked_10m` |
| --- | --- | --- |
| 1,024 set (2 runs) | 96.0 / 95.6% | 95.2 / 96.0% |
| failures with a 40+ decision snag (1,024 set, 2 runs) | 11 / 10 | 7 / 5 |
| bending-route set | 92.3% | 92.9% |
| failures with a 40+ decision snag (bending routes) | 24 | 10 |
| hand-made drop / climb / pocket | 100 / 92 / 100 | 100 / 92 / 100 |

Paired: −0.2 ± 0.5 on the 1,024 set, +0.6 ± 0.7 on bending routes. **The input halves the long snags but not the failures**: the bot gets free and then fails otherwise, so the snags were a symptom. Left off.

## Oct 20 evening: navigate_v3 (mixed loadouts)

`losdetourmix12_30m` = `navigate_v2`'s setup with half the episodes without the extra jump (`--loadouts tsunami,none`; `navigate.scenario_set` made the missing `_j6` sets for train_07–12 from the existing worlds). 3 evaluations each on the 1,024 sets:

| | `navigate_v2` | **`navigate_v3`** | v3 − v2, paired |
| --- | --- | --- | --- |
| with Tsunami | 96.6 / 97.3 / 96.3 | 96.8 / 96.3 / 96.8 | −0.1 ± 0.3 |
| without the extra jump | 86.7 / 86.1 / 85.2 | 88.5 / 88.6 / 87.7 | **+2.3 ± 0.5 (z = 4.2)** |
| bending routes (1 run) | 95.2 (3-run mean) | 95.0 | |
| hand-made drop / climb / pocket | 100 / 100 / 100 | 100 / 93 / 100 | |

**Training on both loadouts costs nothing with Tsunami and gains 2.3 points without it**, at the same 30M budget: mixed equipment does not need much more training (the user's worry from Oct 19), at least for two loadouts. `results/training/navigate_v3/` holds the model; it is the best model for both loadouts.

**Second seed of both changes** (`losdetour_10m_s2`): 95.4% on the 1,024 set, 93.6% on bending routes, hand-made 100 / 96 / 100. Over both seeds, paired per scenario, both changes vs line of sight alone: **+1.2 ± 0.5 on the 1,024 set (z = 2.5)**, +0.2 ± 0.5 on bending routes. The extra bending-route practice helps overall, not on bending routes themselves, once the waypoint is right.

## Oct 20 evening: how diverse are the training scenarios?

The user noticed that most watched scenarios are "hold left or right and jump"; the bot failed one where it had to jump out of honey onto a ledge. `navigate.route_features` traces every scenario's planner route and counts what it passes through. Over all 14,400 training scenarios (12 worlds; the held-out worlds look the same):

| Route passes through or needs | Share |
| --- | --- |
| honey | **0.0%** |
| water | 0.9% |
| dropping through a platform | 1.8% |
| turning back 3+ times (switchbacks) | 0.4% |
| cobwebs | 6.9% |
| turning back at least once | 15.2% |
| climbing 7+ tiles in a row | 20.0% |
| dropping 6+ tiles in a row | 23.5% |

Two causes: starts and targets are placed on dry standable ground, and the planner's liquid costs (water 2, honey 4) route around liquids when it can. Plan agreed with the user: a fixed list of movement categories from the game's mechanics (liquids, webs, platforms, doors, slopes, ice and honey-block floors, ropes, conveyor belts, climbs, drops, gaps, switchbacks), quotas per category in training, targeted generation in the real worlds and randomized obstacle templates for categories the worlds rarely produce, and a held-out test set per category. Failures only point to categories; their scenarios never go into training.

## Oct 20 night: category quotas, and what honey really allows

**Category scenarios.** `generate_category_scenarios` (Common/Scenarios/CategoryScenarios.cs) picks a tile of the category (honey or water pool, cobweb, platform, door, ice or honey-block floor; any spot for switchbacks), a target near it and a start whose planned route has the category. `navigate.category_sets` made a held-out test of 100 per category per held-out world (`eval_0X_cat`, 1,550 scenarios) and training sets of about 300 per category per training world (`train_XX_cat`). `--categories 0.3` makes 30% of the training episodes draw a category uniformly, then a scenario of it.

**Training with the quota** (`cat_10m`, `cat_10m_s2` = `losdetour_10m`, `_s2` + `--categories 0.3`; 10M steps), paired per scenario over both seeds:

| Test | without | with | pooled |
| --- | --- | --- | --- |
| category tests (1,541 feasible) | 93.1 / 92.9 | 92.7 / 94.4 | +0.6, z = 1.4 |
| 1,024 set | 96.6 / 95.4 | 96.9 / 95.7 | +0.3, z = 0.8 |
| bending routes | 93.1 / 93.6 | 93.3 / 93.9 | +0.3, z = 0.6 |
| hand-made | 100 / 96 / 100 both | 100 / 96–97 / 100 | — |

Per category (seed 0 / seed 2): switchback 84.0 → 88.8 / 87.0 → 92.3 (32 vs 15 scenarios, **z = 2.5**); honey 80.8 → 81.9 / 80.8 → 83.4 (z = 1.2); honey_block 93.9 → 84.4 / 88.9 → 95.0 (z = −1.0); the rest stay within a point of 95–100%. **The quota helps switchbacks and nothing else measurably.** The models already handle most categories, so the ceiling is not a lack of exposure.

**The honey_block test is two places.** Its 180 scenarios come from 4 areas of 40 × 40 tiles in eval_01 and 3 in eval_02 (one hive each): 19 of the 20 scenarios `cat_10m` lost there are at one spot. A score on it says whether one hive is solved. Honey is clustered too (23–27 areas per world); the other categories spread over 46–85 areas.

**Honey was partly impossible.** Honey success fell with the route's climb out of the pool (84–95% for climbs of 1–8 tiles, 52% for 9, 39% for 10+; 17 of those 49 were never solved by any of 5 models). The planner gave a jump from honey the same 13 tiles as on land. `navigate.liquid_jump` measures it in the game (bots on the floor of pools 1–14 deep, every pattern of jumps, `results/training/liquid_jump*.json`). The feet rise, in tiles:

| From | one jump | with Tsunami's extra jump (best moment) |
| --- | --- | --- |
| dry floor | 6.1 | 13.1 |
| water, any depth | 4.2–8.3 | 11–17 |
| honey 1–3 deep | 3.1 | 10.1 |
| honey 4+ deep | 3.1 | 6.1–6.5 |
| honey block floor | 1.85 | 8.8 |

In honey the body moves at a quarter of its velocity (Player.HoneyCollision) and without flippers there is no swimming, so from a pool 4+ deep the feet never reach the surface without the extra jump. On a honey block, `Player.sticky` divides the jump height by 10 and the jump speed by 5 (Player.cs:18320) and the run speed by 4; the extra jump fires in the air at full strength.

The planner now uses these numbers: the jump height of a cell comes from its take-off ground (`PathDistanceField.HoneyJumpTiles`, `StickyJumpTiles`; the cell or a non-solid neighbouring column, since the 2-column body can leave shallow honey beside a deep part), and a cell on a honey block costs 4 like a honey cell (its time limit was 4 decisions per tile at a quarter of the run speed, about no slack). With it, 7 honey tests become unreachable, all 7 never solved by any model; no solved one is excluded. One switchback (never solved) also becomes unreachable. Without them `navigate_v3`'s honey score is 83%, not 80.5%. The water results are unchanged (water jumps are as high as dry ones).

**Templates** (`navigate.templates`, in progress): randomized honey pits (3–12 wide, 1–6 deep, sides 0–9 tiles high, often a ledge 4–13 above) and honey-block floors (with a step, a wall, a dry gap or a ledge), each in a sealed vault carved out of solid rock, so no existing scenario's route changes. Sites are boxes of 36 × 26 tiles whose 2-tile shell is rock and whose inside is at least 70% rock and holds no existing scenario's start or target (about 47 per world). A trial on a copy of train_01 (40 vaults, 122 reachable scenarios): `navigate_v3` solved 70 of 77 honey-pit and 45 of 45 honey-floor scenarios.
