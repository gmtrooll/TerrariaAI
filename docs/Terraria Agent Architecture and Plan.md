# Terraria Agent: Architecture and Plan

Oct 14, 2026 · @Vlad Vladutu

## Goal and feasibility

The target is an agent that is given a task in plain words ("get an iron pickaxe", "defeat the Eye of Cthulhu", "beat the game") and carries it out in a fresh world. It is built as a planner over a set of trained skills, because a single network trained end to end on the whole game would almost never see a reward.

**Why one network fails.** A full playthrough takes a skilled player around 50 hours. At 15 decisions per second that is about 2.7 million decisions, with the final reward at the very end. Random exploration never gets there, and even with rewards along the way the agent would have to discover the game's progression on its own.

**Why the hierarchy makes the full game trainable:**

- **Scenario resets.** The mod can start an episode anywhere: in front of a boss with chosen gear, in hardmode, inside the Dungeon. Each skill practises its own part of the game without playing up to it. Nobody plays 50 hours to practise one Moon Lord fight.
- **Reuse.** Navigation is learned once and used by mining, combat and every later task.
- **Known structure stays known.** What to do next (recipes, where items come from, boss order) is in the game's own data. The planner reads it; RL only learns how to do each step.

The same split has recently reached the end of Minecraft: a reported agent beat the Ender Dragon with an LLM planner over a scripted bot library, though on Peaceful with a preselected route (see Key papers).

**The real risk is compounding failure.** A task is a chain of skill calls. If each call succeeds 95% of the time, a 20-call chain succeeds 0.95^20 = 36% of the time, and a 100-call chain 0.6%. A full playthrough is hundreds of calls. So every call is checked against the game state, failures trigger a retry or a new plan, and the main metric is success *with* recovery, not success on the first try.

**What the thesis claims.** The thesis delivers this architecture, built and measured up to a stated stage. The full game is the design target: each stage in the roadmap produces a working, evaluated result, so the thesis holds wherever the work stops.

## Architecture overview

Five layers, and only the skill layer is trained with RL. The planner decides *what* to do from game data, the controller turns each subgoal into skill calls and checks the result, and the skills learn *how* to move, mine and fight.

&#91;embedded content: architecture · 5 layers, 4 learned skills\]

The mod also feeds world memory and the controller's success checks; a failed subgoal goes back to the planner with its reason, and the planner tries another way.

## Components

Eleven parts: four are trained with RL (Navigate, Mine, Combat, Boss fights), the rest are built in code. All learned skills share one observation format and one encoder; they differ in their goal input, action heads, reward and training scenarios. Anything the game already knows (recipes, drops, boss order) is read from game data, never learned.

### 1. Game interface (tModLoader mod)

- **Input:** the running game (headless server, or the client for demos) and commands from Python.
- **Built, not trained:**
  - observation builder (everything in part 2)
  - action executor: raw controls, aim cell, macro calls
  - scenario loader: world, position, inventory, gear, time of day, boss spawned
  - task checkers: has item X, NPC Y dead, standing in region Z
  - event log: pickups, kills, damage dealt and taken, deaths, tiles mined
- **Can do when finished:** run several headless processes with 16 to 64 bots each, start any scenario in under a second, and say whether any goal is met.
- **Evaluated by:** the existing fake-mod tests, a replay check (same actions give the same positions), and steps per second per process.

### 2. Shared observation and encoder

- **Input, per bot and decision:**
  - tile grid, 40 × 40 around the bot, about 10 property channels: solid, platform, slope shape, climbable, damaging, liquid amount and type, mineable and pickaxe power needed, interactable, coarse category
  - entities: the 16 nearest NPCs, projectiles and dropped items, each with relative position, velocity, HP fraction, contact damage, hostile, boss and projectile flags
  - capabilities, about 20 numbers: run speed, jump height, extra jumps, flight and rocket time, dash, grapple reach, liquid abilities, mount stats
  - status: HP, mana, defense, buffs and debuffs, time of day, depth layer, biome
  - inventory: per slot, the item's properties (pick, axe and hammer power, damage, use time, tile it places, stack size), hotbar first
  - explored map: a coarse crop around the bot, for memory beyond the 40-tile window
  - goal: set by the skill being run (target position, target entity, target tile category)
- **Network:** a CNN on the grid, attention pooling over entities and inventory slots, small MLPs on the vectors, then an LSTM of 256 to 512 units. Roughly 2 to 5 million parameters.
- **Training:** learned together with Navigate, then reused as the starting point for later skills. Whether sharing helps is an ablation.

### 3. Navigate (RL skill)

- **Input:** the shared observation; goal = a target position, a region, or an entity to follow.
- **Actions:** left, right, jump (hold for higher), down (drop through platforms), up (climb), grapple with an aim cell, dash.
- **Training:** PPO. Reward = progress along a path-distance field that uses the bot's own jump height and flight time, plus win, death and timeout terms. Scenarios: generated start and target pairs across many worlds and every depth layer, random loadouts, a curriculum by path length, Prioritized Level Replay. Budget estimate 10^7 to 10^8 steps.
- **Can do after training:** reach any reachable point within about 100 tiles in a world it has never seen, using whatever movement items it has, while avoiding lava and spikes.
- **Evaluated by:** success rate and time to target on held-out worlds and held-out items, against a scripted A\* bot.

### 4. Mine (RL skill)

- **Input:** the shared observation; goal = a tile category to collect ("iron ore"), a cell to dig to, or a direction to tunnel.
- **Actions:** Navigate's actions, plus use item at an aim cell (15 × 15 cells around the player) and hotbar choice.
- **Training:** PPO. Reward per target tile mined, a small cost per unneeded tile, death and timeout terms. Scenarios: ore veins at different depths, different pickaxe tiers (including tiles the pickaxe cannot break, where the right move is to stop and report it). Budget estimate 10^7 to 10^8 steps.
- **Can do after training:** collect N tiles of a kind it can see, dig a tunnel to a target, dig itself out when trapped.
- **Evaluated by:** tiles per minute and success rate on held-out ore layouts.

### 5. Combat (RL skill)

- **Input:** the shared observation, mainly the entity list, weapon properties, own HP, defense and potion count; goal = the enemies to clear, or survive for T seconds.
- **Actions:** movement, aim cell, use item, quick heal, hotbar choice.
- **Training:** PPO. Reward = damage dealt − λ × damage taken + a kill bonus − a death penalty. Scenarios: enemy groups spawned by type, biome and time of day, a curriculum from slimes to mixed groups, random gear. Budget estimate 5 × 10^7 to 2 × 10^8 steps.
- **Can do after training:** kill the common enemies of its gear tier, dodge projectiles, back off at low HP.
- **Evaluated by:** win rate and damage taken per kill, against a scripted "face the enemy and swing" bot.

### 6. Boss fights (RL, fine-tuned from Combat)

- **Input:** Combat's input plus boss features: HP, phase, and the arena's layout.
- **Training:** start from Combat's weights and fine-tune in boss scenarios: arena already built, gear of the recommended tier. Either one policy told which boss it faces, or one policy per boss (an experiment in itself). Budget per boss: 10^7 to 10^8 steps, the least certain number in this plan.
- **Can do after training:** defeat a given boss with its recommended gear at a measured win rate.
- **Evaluated by:** win rate per boss and per gear tier.

### 7. Macros (scripted)

- **Built in code, not trained:** craft(item) at a nearby station from the recipe table, equip the best gear by a score, place a tile at a cell, build from a template (shelter, NPC house, arena platforms, a bridge over the Underworld), pick up nearby drops, open a chest and deposit or take, use an item (potion, boss summon), talk to and buy from an NPC, set spawn at a bed.
- **Why scripted:** none of these is the research question, and each is a short function. They use Navigate to walk to stations and build sites.
- **Can do when finished:** each returns success, or failure with a reason ("no workbench nearby", "missing 3 iron bars").
- **Evaluated by:** scenario tests, one per macro.

### 8. Controller (task executor)

- **Input:** the current subgoal from the planner, world memory, game state and event log.
- **Subgoal types:** obtain(item, n), goto(region), mine(tile, n), kill(npc, n), craft(item), equip(), build(template), defeat(boss).
- **Built first as a behavior tree:** maps each subgoal to skill and macro calls with parameters, handles interrupts (enemy close → Combat, HP under 30% → retreat and heal, night without armor → shelter, full inventory → deposit), times out and retries, and reports success or failure with a reason.
- **Research option:** replace the tree with a learned meta-controller that picks skills, trained on subgoal tasks.
- **Can do when finished:** complete one subgoal reliably and stay alive while doing it.
- **Evaluated by:** subgoal success rate and recoveries per hour.

### 9. World memory (code)

- **Input:** observations and events over time.
- **Keeps:** a coarse explored map; landmarks with positions (ore seen, chests, stations, NPC houses, spawn, biome edges, altars, the Dungeon entrance); inventory and chest contents.
- **Can do:** answer "nearest known iron ore" so the agent does not explore the same area twice.

### 10. Knowledge base (game data)

- **Input:** data the mod exports from the game itself: the recipe list (result, ingredients, station), the item drop database, NPC, boss and item stats. Progression (boss order, hardmode unlocks) and short strategy notes are added by hand from the wiki.
- **Built:** a graph linking each item to its recipes, ingredients, stations and sources (tile, NPC drop, chest, shop), and each boss to its summon item and recommended gear.
- **Can do:** answer "how do I get X" as a tree of subgoals, and reject a plan that cannot work.

### 11. Planner

- **Input:** the task text, a text summary of world memory, inventory and progress, knowledge-base answers, and failure reports from the controller.
- **Built in two layers:**
  - a symbolic planner over the knowledge graph turns "obtain" and "defeat" tasks into an exact subgoal tree
  - an LLM turns free-form tasks into subgoals, chooses between options (which ore, which weapon, which boss next) and replans after a failure, from the text summary. It runs only when a subgoal finishes or fails, so its speed hardly matters: a 7 to 8B model at 4-bit fits on the 3060, a larger one runs on the cluster.
- **Training:** none at first (prompts, a fixed subgoal language, a validator). Optional later: fine-tune it with LoRA on logged successful plans.
- **Can do:** turn "defeat the Eye of Cthulhu" into wood → workbench → armor and weapon → lenses from demon eyes → craft the summon item → build an arena → summon at night → fight, and recover when a step fails.
- **Evaluated by:** plan validity, task success on the task suite, and replans per task.

## Roadmap

Stages 0 to 4 are a complete thesis on their own: learned skills that generalize to new worlds and items, put together into short multi-step tasks. Stages 5 and 6 make it a strong one; 7 and 8 continue the same machinery toward the full game.

&#91;embedded content: roadmap · 9 stages, each with a gate\]

Weeks 28 to 34 are kept for writing. The step budgets are estimates; stage 0 replaces them with measured throughput, and every stage starts only after the previous gate is met.

## Evaluation plan

The agent is measured on a fixed task suite of six levels, each run twice: from a fresh world, and from a checkpoint scenario that starts at that level. The checkpoint runs show how good each level is on its own; the fresh-world runs show how failures add up.

| Level | Example tasks | Subgoals per task | Main metric |
| --- | --- | --- | --- |
| L1 Single skill | Reach a point 80 tiles away; mine 10 iron ore; kill 5 zombies | 1 | Success rate, time |
| L2 Short chain | Craft a copper pickaxe from nothing; survive the first night | 3 to 10 | Success rate |
| L3 Early progression | Get an iron pickaxe; reach 200 max HP; build a valid NPC house | 10 to 50 | Success rate, replans per task |
| L4 Pre-hardmode bosses | Eye of Cthulhu, Eater of Worlds or Brain of Cthulhu, Skeletron, Wall of Flesh | 50 to 300 | Win rate, in-game hours |
| L5 Hardmode | Mechanical bosses, Plantera, Golem | Hundreds | Win rate |
| L6 Full game | Defeat the Moon Lord from a fresh world | Thousands | Furthest boss reached |

**Protocol.** 3 to 5 training seeds per method, 50 to 100 evaluation episodes per task, world seeds and some movement items never seen in training. Results as interquartile mean with confidence intervals (Agarwal et al. 2021). A recorded video for every reported task.

**Baselines:**

- a scripted bot (A\* paths plus hand rules) on L1 to L3
- flat PPO on the same tasks with no hierarchy, to show what the hierarchy buys
- the planner calling macros only, with no learned skills

**Ablations:** no capability vector, no curriculum, no shared encoder, symbolic planner only versus symbolic plus LLM.

## Hardware plan

The PC is enough to build and train every skill one at a time; the cluster is worth it for running many seeds and ablations in parallel and for larger language models, but only if the game can run there headless. Assumed: RTX 3060 with 12 GB of video memory, 12 CPU cores, 16 GB of RAM.

| Job | Where | Notes |
| --- | --- | --- |
| Headless game servers | PC CPU | Cores allow about 8 to 10 processes; 16 GB of RAM probably limits it to 4 to 6. Measure RAM per process. 32 GB of RAM is the cheapest speed-up available. |
| PPO for one skill | PC GPU | A 5M-parameter policy needs under 2 GB of video memory. The GPU waits on the game most of the time. |
| Seeds and ablations | Cluster | The evaluation needs 3 to 5 seeds × several methods. Each run is a separate job, so this is where the cluster pays off most. |
| LLM planner | PC GPU or cluster | A 7 to 8B model at 4-bit uses about 5 to 6 GB and fits on the 3060 during evaluation. Larger models, or an API, for comparison. |
| LoRA fine-tuning of the planner, behavior cloning, world models | Cluster GPU | Optional research directions; too slow on a 3060. |
| Navigation simulator, if built | Either | A JAX simulator runs thousands of environments on one GPU. |

**Questions for the cluster admins:**

- CPU cores and RAM per node: the game servers need cores, not GPUs. A GPU node with few cores will not train faster against the real game.
- Maximum job length and whether jobs can be resumed.
- Whether the .NET runtime can be installed, or containers (Docker or Apptainer) are allowed, to run the tModLoader server on Linux.
- Whether you may copy your own Terraria and tModLoader files to the nodes.

## Risks and mitigations

The two risks most likely to cut the plan short are the headless server and the boss-fight budgets; both have a fallback that keeps the thesis intact.

| Risk | What it breaks | Mitigation |
| --- | --- | --- |
| Fake bots do not run on a headless server | Throughput stays at one game client | Keep client mode; build the navigation simulator for bulk training and use the real game for fine-tuning and evaluation |
| Long chains fail as errors add up | Fresh-world runs stop early | Checks after every call, retries and replanning; report checkpoint results per level next to fresh-world ones |
| Boss fights need far more steps than estimated | Stage 6 slips | Easier boss variants first (lower HP and damage, set by the scenario loader), a warm start from your own recorded play, scripted help (arena, potions) |
| Skills interfere when they share the encoder | Each skill gets worse | Keep separate networks; sharing stays an ablation |
| Reward hacking, such as oscillating to farm progress reward | Wrong behavior that still scores well | Potential-based shaping, a cap on progress per step (already in place), video review of every evaluation |
| The LLM writes plans that cannot work | Wasted episodes | Fixed subgoal language, a validator against the knowledge base, the symbolic planner handles obtain and defeat tasks |
| tModLoader updates change hooks | The mod breaks mid-project | Pin one tModLoader version for the whole thesis |
| Scope keeps growing | Nothing is finished when the thesis is due | Hard stage gates; stages 0 to 4 are the minimum, everything after is extra |

## Key papers per component

The closest prior work to this architecture is Plan4MC and DEPS in Minecraft: RL-trained or learned skills under a planner that reads the game's structure.

| Component | Paper | Why it matters here |
| --- | --- | --- |
| Whole architecture | [Plan4MC](https://arxiv.org/abs/2303.16563), Yuan et al. 2023 | RL-trained basic skills plus a skill graph for planning; long Minecraft tasks |
| Whole architecture | [DEPS](https://arxiv.org/abs/2302.01560), Wang et al. 2023 | Describe, explain, plan, select: an LLM planner that replans after failures |
| Whole architecture | [JARVIS-1](https://arxiv.org/abs/2311.05997), Wang et al. 2023 | Planner with a memory of past experience; many Minecraft tasks |
| Planner and skill library | [Voyager](https://arxiv.org/abs/2305.16291), Wang et al. 2023 | LLM writes and stores skills as code over a game API |
| Knowledge base | [DECKARD](https://arxiv.org/abs/2301.12050), Nottingham et al. 2023 | An abstract crafting graph guides exploration and planning |
| Planner on a 2D crafting game | [SPRING](https://arxiv.org/abs/2305.15486), Wu et al. 2023 | GPT-4 plays Crafter after reading its paper |
| LLM-suggested goals for RL | [ELLM](https://arxiv.org/abs/2302.06692), Du et al. 2023 | LLM proposes goals; RL learns them in Crafter |
| Controller | [h-DQN](https://arxiv.org/abs/1604.06057), Kulkarni et al. 2016 | A learned meta-controller choosing subgoals for a low-level policy |
| Controller | [Options framework](<https://doi.org/10.1016/S0004-3702(99)00052-1>), Sutton, Precup and Singh 1999 | The theory of skills as temporally extended actions |
| Skill training | [PPO](https://arxiv.org/abs/1707.06347), Schulman et al. 2017 | The algorithm for every RL skill |
| Skill training | [Prioritized Level Replay](https://arxiv.org/abs/2010.03934), Jiang et al. 2021 | Automatic curriculum over scenarios |
| Generalization | [Procgen](https://arxiv.org/abs/1912.01588), Cobbe et al. 2020 | 2D platformers: more training levels give better generalization |
| Loadouts | [Domain randomization](https://arxiv.org/abs/1703.06907), Tobin et al. 2017 | Train across randomized conditions to generalize |
| Loadouts | [RMA](https://arxiv.org/abs/2107.04034), Kumar et al. 2021 | Infer your own dynamics from recent movement |
| Benchmark closest to Terraria | [Craftax](https://arxiv.org/abs/2402.16801), Matthews et al. 2024 | 2D survival-crafting; memory is the biggest lever |
| Learning from play videos | [VPT](https://arxiv.org/abs/2206.11795), Baker et al. 2022; [NitroGen](https://arxiv.org/abs/2601.02427), Magne et al. 2026 | Warm starts from recorded or internet gameplay |
| World models | [DreamerV3](https://arxiv.org/abs/2301.04104), Hafner et al. 2023 | Option for training with fewer real game steps |
| Throughput | [Sample Factory](https://arxiv.org/abs/2006.11751), Petrenko et al. 2020 | Fast asynchronous RL on a single machine |
| Evaluation | [Statistical precipice](https://arxiv.org/abs/2108.13264), Agarwal et al. 2021 | How to report RL results across seeds |

Not a paper, but relevant to feasibility: on 20 September 2026 a [news report](https://ascii.jp/en/elem/000/004/436/4436545/) described an agent that defeated Minecraft's Ender Dragon with an LLM planner, an action-choosing model and a scripted bot library, on Peaceful difficulty with a preselected seed and route.
