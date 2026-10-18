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

## Results

(filled in as runs finish)
