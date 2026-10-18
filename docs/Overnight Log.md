# Overnight Log

Oct 18–19, 2026. Decisions taken while you were away, why, and the results. Newest entries at the bottom of each section.

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

## Measurements

- **Environment speed with bots spread over real scenarios:** about 13 ms per step for 32 bots on one server (about 2,500 steps/s per server). That is roughly twice the benchmark's per-bot cost.
  - The benchmark kept all bots near the spawn point.
  - Spread over the world, every bot reads different memory: cache misses in both the game update and the observation.
  - Inside a step, the observations take 3.4 ms (32 bots) and the distance fields almost nothing.
- **Scripted baseline:** walk toward the target, jump when stuck or when the target is above. 71% success on the 5–15 band, enough to unlock the 15–30 band. A learned policy should beat this.

## Results

(filled in as runs finish)
