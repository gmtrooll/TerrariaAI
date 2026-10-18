# Action Design

Oct 18, 2026

What the policy outputs each decision. The game's controls are read from the decompiled code, `Player.cs` unless noted. As with the observation and learner documents, these are options and recommendations for discussion.

## What the game reads

Each tick, `Player.Update` reads a set of control flags (`Player.cs:23670` onward). A human's keyboard and mouse set them through `TriggersSet.CopyInto`; our bots get them through `SetControls` (`BotInputOverride`).

| Control | What it does in the game |
| --- | --- |
| `controlLeft`, `controlRight` | Run. Holding both: left is checked first in `HorizontalMovement`, so the pair acts as one three-way choice (left, none, right) |
| `controlJump` | Jump: holding it keeps the upward speed for up to `jumpHeight` ticks (15 by default, `Player.cs:19550`), so jump height depends on how long it is held. Starts extra jumps, wing flight and rocket boots in the air |
| `controlDown` | Drop through platforms (`fallThrough`, `Player.cs:26888`), climb down ropes, cancel featherfall, use stairs |
| `controlUp` | Climb ropes (`FindPulley`), flip gravity with a gravity potion, step onto platforms |
| `controlHook` | Throw a grappling hook toward the cursor (`QuickGrapple`) |
| `controlUseItem` + cursor position | Use the held item at the cursor: swing, shoot, mine, place. Reach for mining and placing is about ±6 tiles horizontally, 4 above, 2 below (`Player.cs:46572`) |
| `selectedItem` | Hotbar slot 0–9 |
| `controlMount`, `controlQuickHeal`, `controlQuickMana`, `controlThrow`, `controlSmart`, inventory and map keys | Mount, heal potion, mana potion, throw item, smart cursor, interface |

Timing details that matter for the choice:

- **Dash:** there is no dash key. A dash is a second press of left or right within 15 ticks of the first (`DoCommonDashHandle`, `Player.cs:20120`).
- **Jump release:** a new jump needs the key released first (`releaseJump`), unless the bot has auto-jump gear.
- **Item swings:** an item swing takes its use time (about 15–30 ticks for early weapons and tools). Holding use repeats it only for items with auto-reuse.

## Decision 1: the shape of the output

The policy outputs a probability distribution, and an action is sampled from it. A keyboard-like game has three common shapes:

| Shape | How | For | Against |
| --- | --- | --- | --- |
| A. One list of combinations | Every combination of keys is one choice (today: 8 combinations of left/right/jump) | Simplest; the network learns combinations directly | Grows multiplicatively: movement × jump × up/down × hook × use × aim × hotbar is millions |
| B. One head per control (factorized) | Separate choices for horizontal, vertical, jump, hook, use, aim, hotbar; an action is one pick from each | Grows additively; each head can be masked or added on its own | Assumes the parts can be chosen independently given the observation (usually fine; the shared network still coordinates them) |
| C. Continuous outputs | E.g. aim as a continuous x, y | Natural for aiming | Harder to train with PPO than discrete choices; aim reach is small enough to be discrete |

**Recommendation: B.**

- **Horizontal:** three choices (left, none, right), so the impossible "both" never happens.
- **Vertical:** three choices (up, none, down).
- **Jump:** two choices.

Later skills add heads without changing the existing ones:

- **Hook:** two choices, aimed with the aim head.
- **Use:** two choices.
- **Aim cell:** one cell of the reach window, about 13 × 11 = 143 tiles around the player: about 6 tiles left and right, 4 above the head, 2 below the feet. Either one joint choice of 143, or two heads, x (13) and y (11).
- **Hotbar:** 11 choices, ten slots plus "keep".

The learner design already plans for multiple heads and masking (decision 7 there).

## Decision 2: how often the policy decides

Today the policy decides every 4 ticks (`frame_skip` 4, 15 decisions per second) and each action is held for those 4 ticks.

| Interval | Decisions per second | For | Against |
| --- | --- | --- | --- |
| 2 ticks | 30 | Finer jump heights, faster reactions (dodging) | Twice the learner load per second of play; twice the observation cost |
| 4 ticks (now) | 15 | About a fast human's rate of distinct inputs | Jump heights come in steps of 4 ticks; a 1-tick tap is impossible |
| 6–8 ticks | 7–10 | Cheaper; longer horizons per decision | Coarse control, missed reactions |

The jump detail is the one that matters most for Navigate. Holding jump for 1, 2, 3 or 4 decisions at 4 ticks each gives 4, 8, 12 or 15 ticks of upward thrust. Between those steps there is no in-between height. A human can tap for 1–2 ticks for a small hop. Whether 4-tick steps are fine enough is an empirical question.

A middle path is an extra output choosing how long the jump key is held inside the next decision: 1, 2, 3 or 4 ticks. Jump height becomes precise without deciding more often.

**Recommendation:** keep 4 ticks, add the jump-duration option as an experiment, and compare 2, 4 and 8 ticks on Navigate (success against wall-clock time). This needs no protocol change beyond sending the interval at the handshake, as now.

## Decision 3: Navigate's actions

| Head | Choices | Why Navigate needs it |
| --- | --- | --- |
| Horizontal | left, none, right | Running; double taps give dashes when the gear has one |
| Vertical | up, none, down | Ropes and chains (up and down), dropping through platforms (down), stairs. The old bots had no down key, so platforms were one-way walls for them |
| Jump | no, yes | Jumping, extra jumps, wings, rocket boots |

18 combinations in total, against 8 today. The planner's movement model (`PathDistanceField`) also learns about ropes and dropping through platforms, so its distances match what the bot can do.

The hook is left for later: it needs aiming, which belongs with the aim head of Mine and Combat.

## Decision 4: fixed or learned action repeat

Instead of deciding every N ticks, the policy could choose an action and how long to keep it ("temporally extended actions", or options in RL terms). Fewer decisions means cheaper learning and longer horizons, but more complex training: variable-length steps make discounting and advantages harder.

**Recommendation:** not in v1. The jump-duration option above covers the one place where finer timing clearly matters.

## Open questions for discussion

1. Factorized heads (B) with horizontal, vertical and jump for Navigate?
2. Keep 4-tick decisions, and test the jump-duration option plus 2- and 8-tick intervals as experiments?
3. Hook only once aiming exists (Mine and Combat), or a simple version for Navigate earlier (hook straight up or toward the target)?
