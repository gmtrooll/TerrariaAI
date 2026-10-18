# Overnight Log

Oct 18–19, 2026. Decisions taken while you were away, why, and the results. Newest entries at the bottom of each section.

## Summary for the morning

1. **The custom PPO matches SB3** on the same environment and network: the learning curves overlap, the held-out results are 68% vs 70%, and it is faster. It replaces SB3 (Results 1, 3).
2. **The waypoint was missing, and it matters most.** The agreed design gives Navigate the next route waypoint, but the code only gave the final target. With the waypoint: **78% vs 64%** on unseen worlds, and 65% vs 46% on 60–120 tile routes (Results 4, 6).
3. **Evaluate with sampled actions.** Always taking the most likely action makes the same policy look like the scripted baseline (49% vs 68%), because it gets stuck. There is no gap between training and unseen worlds (Results 3).
4. **The planner had two errors, both from not matching the game:** the player is 2 tiles wide, not 1 (`Player.width = 20`), and bots can drop through platforms. Both are fixed and all scenario sets regenerated (Results 2).
5. **Experiments, one seed each (Results 6):**
   - Stride-2 first convolution: same quality, +31% speed → recommended default.
   - Visibility all / exposed / exposed+memory: 75 / 78 / 79%, no real difference → the fair observation costs nothing.
   - 40×40 grid without the overview: same overall, +24% speed, a little lower on the longest routes → needs a second seed.
   - Full 120×68 grid: 76%, no better and 3.4× slower (645 steps/s) → drop it. (First attempt ran out of GPU memory; rerun with gradient accumulation, same minibatch of 4,096 in pieces of 1,024.)
6. **Speed:** the learner is still the bottleneck. bf16 + overlap gives 1,525 → 2,200 steps/s, and stride 2 brings it to 2,850. Overlap is held back by collection and update sharing one Python process; collecting in a separate process is the next speed fix.
7. **Best held-out result so far: 79%** (stride 2, and memory). The best scripted baseline gets 54%, random 1%.

Questions for you:
- Make stride 2 the default? Run second seeds for square40 vs the two-resolution grid?
- Navigate is good enough to move on to other parts (controller / planner integration) once the longest routes improve. Or should it be pushed further first (longer training, more worlds)?

## Decisions

| # | Decision | Why | Easy to change? |
| --- | --- | --- | --- |
| 1 | Action output as in `docs/Action Design.md`: three heads (horizontal left/none/right, vertical up/none/down, jump), one decision every 4 ticks, no hook yet | The recommendations of that document; you had not answered yet | Yes: the heads are separate in both the mod and the learner |
| 2 | Scenarios generated automatically from the planner's path search (`Common/Scenarios/ScenarioGenerator.cs`), not from the 11 hand-placed zones | The zones are all in one personal world (`aTestingSP2`) and too few | — |
| 3 | Jump limit for the planner: 13 tiles | Measured in game: 6.13 tiles for a single jump, 13.12 with the bots' Tsunami in a Bottle (the old setting of 14 was an estimate) | Yes, an argument |
| 4 | 6 training worlds (`train_01`–`train_06`, seeds 101–106) and 2 held-out evaluation worlds (`eval_01`, `eval_02`, seeds 901–902), all small | Evaluation on terrain never trained on measures generalization, the old setup's main weakness | Yes |
| 5 | 1,200 scenarios per world: 400 per layer (surface, underground, cavern), 100 per distance band (5–15, 15–30, 30–60, 60–120 path tiles) | Even coverage for a curriculum ordered by path distance | Yes |
| 6 | The planner (`PathDistanceField`) now limits sideways movement through air: only within 5 tiles of ground the bot could jump from (gaps up to about 11 tiles can be crossed); all scenario sets regenerated with it | Your remark: some pairs looked unreachable. The old model let the bot walk across the open sky. The same model gives the training reward, so the reward no longer favours impossible routes either | Yes, the `max_air_gap` argument |
| 7 | Scenarios no policy ever solves are tracked (`scenario_stats.json` per run) rather than guaranteed reachable in advance | A full guarantee needs real movement physics, not a grid model; scenarios never solved across runs can be dropped | — |
| 8 | Navigate runs without enemies: servers start with `npc_spawns=0`, and enemies are cleared when the learner connects. Random tile updates are off (the movement profile) | Navigate should learn movement, not survival; the entity slots stay empty, as decided | Yes, a launch argument |
| 9 | Episode time limit: 40 + 4 × path distance decisions | A bot covers about 0.75 tiles per decision at walking speed, so this is generous without letting bots wander for minutes | Yes |
| 10 | Reward as before: +20 win, −5 death, −1 timeout, +0.5 per tile of path progress (at most 3 tiles per decision); timeouts are bootstrapped (not treated as real ends) | Keeps results comparable with the old setup | Yes |
| 11 | Curriculum: bands open one at a time when the hardest open band reaches 50% success over 200 episodes; half the episodes from the hardest open band, half from all open bands | Simple and transparent; adaptive sampling (Prioritized Level Replay) can come later | Yes |
| 12 | PPO settings for all runs unless noted: 128 steps per rollout per bot, minibatch 4096, 4 epochs, learning rate 2.5e-4, γ 0.99, entropy 0.01; 6 servers × 32 bots = 192 bots | Middle-of-the-road settings for this rollout size; the same for SB3 and the custom PPO | Yes |
| 13 | The custom PPO replaces SB3 for all further runs | It matched SB3's learning curve on the parity test (Results 1) and is faster | — |
| 14 | Planner: the player's body is 2 tiles wide, and it can drop through platforms; all scenario sets regenerated | Both followed from reading the game (`Player.width = 20`) and the action set (Results 2) | Yes |
| 15 | Experiment runs: 5M steps each, all with bf16 and overlapped collection, compared against a reference run with the same speed options (`fast_fovea_exposed`); one seed each | The night fits about six runs; one seed is enough to spot large differences, not small ones | Yes, `Training/navigate/overnight_queue.sh` |
| 16 | Held-out evaluation samples actions instead of taking the most likely one | The most likely action gets stuck: 49% vs 68% for the same policy (Results 3) | Yes, `--deterministic` |
| 17 | The goal input carries the route waypoint (12 path tiles ahead) besides the final target | Implements the agreed "planner sets the route"; the target alone misleads on long cave routes (Results 4) | Yes, `-obs waypoint=0` |

## Measurements

- **Environment speed with bots spread over real scenarios:** about 13 ms per step for 32 bots on one server (about 2,500 steps/s per server). That is roughly twice the benchmark's per-bot cost.
  - The benchmark kept all bots near the spawn point.
  - Spread over the world, every bot reads different memory: cache misses in both the game update and the observation.
  - Inside a step, the observations take 3.4 ms (32 bots) and the distance fields almost nothing.
- **Learner speed options in training** (custom PPO, 192 bots, per iteration of 24,576 steps):

  | | Update | Collection | Steps/s |
  | --- | --- | --- | --- |
  | fp32, no overlap (`parity_custom`) | 11.4 s | 4.8 s | 1,525 |
  | bf16 + overlap (`fast_fovea_exposed`) | 7.6 s | 13.1 s (in parallel with the update) | 1,900 |

  bf16 gives 1.5× on the update. Overlap helps less than expected: collection runs in a thread of the same Python process as the update, and the two slow each other down (likely Python's global interpreter lock, the GIL), so it takes almost 3× longer. The fix is to collect in a separate process; not done tonight.
- **Scripted baseline:** walk toward the target, jump when stuck or when the target is above. 71% success on the 5–15 band, enough to unlock the 15–30 band. A learned policy should beat this.

## Results

### 1. Parity: custom PPO vs Stable-Baselines3 (same environment, network and settings, 5M steps each)

![parity](../results/training/parity.png)

| | SB3 | Custom |
| --- | --- | --- |
| Win rate per band at 4.9M steps (5–15 / 15–30 / 30–60 / 60–120) | 87 / 82 / 72 / 54% | 88 / 82 / 73 / 59% |
| All four bands open after | ~350k steps | ~350k steps |
| Throughput | 1,340–1,480 steps/s | 1,525 steps/s |
| Wall-clock for 5M steps | 59 min | 54 min |

**Verdict: the custom PPO learns the same as SB3.** The curves lie on top of each other from start to finish, so it replaces SB3 from here on. Both use fp32 and no overlap here; the speed options are tested next.

Where the time goes in the custom run: per iteration (24,576 steps), the update takes 11.4 s and the collection 4.8 s. **The learner, not the game, is the bottleneck**: the 6 servers would deliver about 5,100 steps/s if the learner kept up.

### 2. Scenarios no policy solved (from SB3's `scenario_stats.json`)

Scenarios tried at least 4 times and never solved, by band: 5–15: 102 of 1,010; 15–30: 42 of 278; 30–60: 63 of 264; 60–120: 532 of 1,745. Caverns dominate (for 60–120 cavern, 235 of 575).

Checks:
- **Is the target a real standing spot?** A bot placed on each never-solved target of `train_01` (142 scenarios) and left for 90 ticks drifts more than 1.5 tiles in only 11 cases, against 3 for solved targets. Mostly yes.
- **Is the planner's movement model right?** Reading it against the game showed two mismatches:
  - It treated the player as 1 tile wide; in the game it is 20 px (`Player.width = 20`), so the body always covers 2 columns and one-tile shafts and notches are impossible. Fixed: a cell is passable only if it and a neighbouring column are clear for 3 rows (same in `Standable`).
  - It forbade dropping through platforms, written when bots had no "down" key; the action set has one now. Fixed.

After the parity runs, all 8 scenario sets were regenerated with the corrected planner, and all runs after this point train on the new sets.

### 3. Held-out evaluation: sample actions, don't take the most likely one

Held-out worlds `eval_01` and `eval_02`, 512 fixed scenarios (64 per band per world), old scenario lists:

| Policy | Overall | 5–15 | 15–30 | 30–60 | 60–120 |
| --- | --- | --- | --- | --- | --- |
| Custom, most likely action | 49% | 73% | 59% | 42% | 23% |
| SB3, most likely action | 52% | 75% | 59% | 44% | 28% |
| **Custom, sampled actions** | **68%** | **80%** | **73%** | **72%** | **46%** |
| SB3, sampled actions | 70% | 84% | 77% | 71% | 47% |
| Scripted: walk toward the target, jump when stuck | 50% | 77% | 59% | 41% | 22% |
| Random | 1% | 2% | 0% | 0% | 1% |
| Custom, most likely action, on 2 training worlds | 51% | 80% | 47% | 49% | 27% |

- **No generalization gap:** with sampled actions the policy succeeds on unseen worlds as often as during training (68%).
- **Taking the most likely action gets the bot stuck**: it repeats the same failing move against a wall forever, where sampling eventually tries something else. It does as badly on the training worlds, so this is not overfitting.
- Decision 16: evaluation samples actions by default (`--deterministic` for the other mode).

### 4. The goal input carried only the final target, not the route waypoint

`docs/Observation Design.md` records "Navigate learns local movement. The planner sets the route, and the goal input carries the next waypoint", but the implementation only gave the final target. On a 60–120 tile cave route the straight line to the target often points into rock.

Decision 17, implemented: the planner's distance field now stores, per cell, the next step toward the target. Each decision the environment walks 12 steps along it from the bot's feet, and the observation gets 5 more goal numbers: `has_waypoint`, the clipped offset and the direction to that waypoint. `-obs waypoint=0` turns it off. That switch is used for the `no_waypoint` run, which measures what the waypoint is worth. A matching scripted baseline walks toward the waypoint instead of the target (`--policy greedy_wp`).

The old parity models cannot run on the new observation (11 goal numbers instead of 6), so their results stay on the old lists. The baselines are re-evaluated on the new lists.

### 5. Baselines on the new held-out lists (corrected planner, sampled scenarios as in Results 3)

| Policy | Overall | 5–15 | 15–30 | 30–60 | 60–120 |
| --- | --- | --- | --- | --- | --- |
| Scripted, toward the target | 54% | 79% | 67% | 41% | 27% |
| Scripted, toward the waypoint | 50% | 79% | 63% | 34% | 23% |
| Random | 1% | 2% | 2% | 0% | 0% |

Following the waypoint does not help the *scripted* policy. Routes traced along the waypoint chain look plausible (rendered for long cavern scenarios): they follow floors, water surfaces and walls. The scripted rule is the weak part: "jump if the point is above" fails when the route goes straight up a wall or ledge, where it needs a sideways step first. The learned run with and without the waypoint (`fast_fovea_exposed` vs `no_waypoint`) is the real test.

### 6. Experiments (custom PPO, bf16 + overlap, 5M steps, new scenario sets; held-out = new lists, sampled actions)

| Run | What differs | Held-out overall | 5–15 | 15–30 | 30–60 | 60–120 | Cavern | Train win (last iter.) | Steps/s |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `fast_fovea_exposed` | reference | **78%** | 90% | 88% | 70% | 65% | 68% | 78% | 2,173 |
| `no_waypoint` | grid=fovea,visibility=exposed,waypoint=0 | **64%** | 80% | 70% | 62% | 46% | 54% | 70% | 2,167 |
| `stride2` | stride 2 | **79%** | 90% | 84% | 77% | 66% | 71% | 76% | 2,853 |
| `vis_all` | grid=fovea,visibility=all | **75%** | 89% | 84% | 70% | 59% | 62% | 72% | 2,174 |
| `vis_memory` | grid=fovea,visibility=exposedmemory | **79%** | 90% | 89% | 75% | 64% | 69% | 79% | 2,148 |
| `grid_square40` | grid=square40,visibility=exposed | **78%** | 91% | 89% | 76% | 58% | 69% | 76% | 2,701 |
| `grid_full` | grid=full,visibility=exposed | **76%** | 90% | 88% | 70% | 58% | 68% | 77% | 662 |

Reference: 41×41 grid + 120×68 overview, exposed tiles, waypoint on, first convolution stride 1. "Train win" is the last iteration's success over all open bands; "Steps/s" the median.

Findings:
- **The route waypoint is worth +14 points on held-out worlds** (78% vs 64%). The gain is largest on long routes (60–120: 65% vs 46%) and in caverns (68% vs 54%). It also learns faster ([curves](../results/training/waypoint.png)). The planner-sets-the-route split works as intended.
- **First convolution stride 2 costs nothing in quality and gives +31% throughput** (79% vs 78% held-out, 2,850 vs 2,170 steps/s). The difference in success is within one seed's noise. Recommendation: stride 2 as the default from now on (not changed tonight, so that the remaining runs stay comparable with the reference).
- **Seeing every tile (no fairness limit) does not help**: 75% vs 78% with exposed tiles only, and caverns 62% vs 68%. The difference is within the noise of one seed. If anything, the hidden tiles inside rock are clutter. The fair observation costs nothing for movement.
- **Tile memory makes no measurable difference** (79% vs 78%). Short Navigate episodes rarely come back to places seen earlier, so memory matters little here. It may matter for longer tasks (exploration, returning home). Keep it as an option, off by default for Navigate.
- **A single 40×40 grid without the overview does as well overall** (78%) and is 24% faster (2,700 steps/s; no overview to build or encode). It is lower on the longest band (58% vs 65%), possibly because the overview helps on long routes, but one seed can't separate that from noise. With the waypoint carrying the route, the overview matters less than it would otherwise. Worth a second seed of both before choosing.
- **The full 120×68 grid is no better and 3.4× slower** (76% vs 78%, 662 steps/s). The extra detail far from the bot does not help movement, as expected. Not worth pursuing.

All curves: [experiments.png](../results/training/experiments.png).
