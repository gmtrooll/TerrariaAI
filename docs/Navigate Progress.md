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
