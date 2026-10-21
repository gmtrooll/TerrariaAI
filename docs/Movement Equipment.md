# Movement Equipment

This is a catalogue of every vanilla item, buff and mount in tModLoader 1.4.4 that changes how a player moves. It was read from the decompiled source on Oct 21, 2026, and is meant for a Navigate policy that can use any movement equipment.

**Where the code is.** Every path is under `tml-reference/src/Terraria/`. Abbreviations:

| Short | File |
| --- | --- |
| P | `Player.cs` |
| Pr | `Projectile.cs` |
| M | `Mount.cs` |
| I | `Item.cs` |
| C | `Collision.cs` |
| IID | `ID/ItemID.cs` |
| EJL | `ModLoader/ExtraJumpLoader.cs` |
| WSI | `Initializers/WingStatsInitializer.cs` (fills `ArmorIDs.Wing.Sets.Stats`) |
| BMS | `FirstMod_Sp/Common/Players/Systems/BotManagerSystem.cs` (our mod, not the game) |

The extra-jump classes are in `ModLoader/` (for example `ModLoader/CloudInABottleJump.cs`), not in `ModLoader/Default/`.

**Units.** Speeds are px per tick, times are ticks. 16 px = 1 tile and 60 ticks = 1 s. The player is 20 × 42 px. Values marked **derived** are my own arithmetic or a small simulation of the code order; they are not stated in the code and were not measured in game. All derived values ignore collisions and ceilings.

**The bot.** It picks one action every 4 ticks from three heads: horizontal (left / none / right), vertical (up / none / down) and jump (yes / no). An action is therefore held for at least 4 ticks. A "fresh press" means the control was off on the previous tick, which for the bot means one "no" decision (4 ticks) before the "yes".

---

## 1. Base movement and where it is set each tick

### 1.1 Base values

| Field | Value | Declared | Reset every tick in `Player.Update` |
| --- | --- | --- | --- |
| `gravity` | 0.4 (`static defaultGravity`) | P:2876, 2885 | P:23348 |
| `maxFallSpeed` | 10, then `+= 0.01`, so the real cap is 10.01 | P:2887 | P:23347, 23415 |
| `jumpHeight` (ticks of held lift) | 15. **static** | P:2878 | P:23349 |
| `jumpSpeed` (lift speed) | 5.01. **static** | P:2883 | P:23350 |
| `maxRunSpeed` | 3 | P:2901 | P:23351 |
| `runAcceleration` | 0.08 | P:2915 | P:23352 |
| `runSlowdown` | 0.2 | P:2917 | P:23353 |
| `accRunSpeed` (sprint cap, used by boots) | = maxRunSpeed = 3 | P:1290 | P:23354 |
| `moveSpeed` | 1 | P:2816 | `ResetEffects`, P:17176 |

**Liquids and other states override these values** right after the reset (P:23361-23414):

| State | gravity | maxFallSpeed | jumpHeight | jumpSpeed | Code |
| --- | --- | --- | --- | --- | --- |
| Shimmer liquid | 0.15 | – | 23 | 5.51 | P:23372-23377 |
| Shimmering (buff 353) | ×0.9 | ×0.9 | – | – | P:23367-23371 |
| Honey | 0.1 | 3 | – | – | P:23381-23385 |
| Merman (Neptune's Shell in water) | 0.3 | 7 | – | – | P:23386-23390 |
| Trident held (277), not lava | 0.25 (0.1 holding up) | 6 (2 holding up) | 25 | 5.51 | P:23391-23402 |
| Water or lava | 0.2 | 5 | 30 | 6.01 | P:23403-23409 |
| Vortex debuff | 0 | – | – | – | P:23411-23414 |
| Near the top of the world | × factor 0.25 … 1, depending on height | – | – | – | P:23579-23601 |

### 1.2 Order of one `Player.Update` (P:23066)

1. Base reset and liquid values (P:23347-23415).
2. Input, **local player only** (`i == Main.myPlayer`, P:23670-24323):
   - `ResetControls` (P:23672), then copy keyboard input (P:23677).
   - Mount key → `QuickMount` (P:23778).
   - Double-tap timers for armor set bonuses (P:23906-23957).
   - **`PlayerLoader.SetControls`** (P:23959). This is where our bots set their controls (BMS:37).
3. Fall-damage check, local player only (P:24324-24395).
4. `ResetEffects` (P:24516): it clears every accessory flag.
5. `UpdateBuffs` (P:24580), `UpdateEquips` (P:24652), mount effects (P:24675), `UpdateArmorSets` (P:24702).
6. `runAcceleration *= moveSpeed; maxRunSpeed *= moveSpeed` (P:24967-24968). **`accRunSpeed` and `runSlowdown` are not scaled by moveSpeed.**
7. `UpdateJumpHeight` (P:24969).
8. Extra jumps: refreshed on ground or wall-slide, consumed when a mount blocks them (P:24999-25010).
9. If not grappled (P:25404):
   - Wing air tweaks, floor and armor multipliers (P:25406-25483).
   - Extra-jump speed multipliers (P:25483), carpet (P:25484-25488).
   - **Mount override** (P:25494-25503), `PostUpdateRunSpeeds` (P:25533).
   - `HorizontalMovement` (P:25534).
   - Gravity flip (P:25536-25583).
   - `JumpMovement` (P:25590), `DashMovement` (P:25603), `WallslideMovement` (P:25604), `CarpetMovement` (P:25605).
   - Wing refill (P:25616), wings (P:25621-25650), rocket boots (P:25652-25779).
   - Mount flight, Featherfall, glide or plain gravity (P:25780-26172), fall-speed caps (P:26173-26205).
10. Hook key → `QuickGrapple`, local player only (P:26290-26303).
11. `GrappleMovement` (P:26482), `StickyMovement` (cobweb and honey block).
12. `releaseLeft` / `releaseRight` / `releaseDown` updated (P:26447-26465).
13. Collision (P:26968-26996). Liquid flags (P:26562), wall contact `slideDir` (P:27019-27027) and floor flags (`FloorVisuals`, P:27047) are set **here, after movement, so they apply on the next tick**.
14. `grappling[0] = -1; grapCount = 0` (P:27079-27080). Hook projectiles re-register after all players update.

### 1.3 Plain jump and run (no equipment)

- **Starting a jump** (P:19572-19607) needs all of:
  - jump pressed with `jump <= 0`;
  - on the ground, wall-sliding, or an extra jump available;
  - `releaseJump` (a fresh press), or `autoJump` while on the ground.

  The start sets `velocity.Y = -jumpSpeed` and `jump = jumpHeight`.
- **While jump is held and `jump > 0`:** `velocity.Y = -jumpSpeed` and `jump--` every tick (P:19550-19570). Gravity is still added afterwards.
- **Releasing jump:** `jump = 0` (P:19615-19620). This is what makes the jump height variable.
- **Hitting the ceiling** also sets `jump = 0` (P:22248-22251).
- **Running** (`HorizontalMovement`, P:18442):
  - With a direction held: `+runAcceleration` per tick up to `maxRunSpeed`. This works in the air too (P:18556, 18625).
  - Between `maxRunSpeed` and `accRunSpeed`, the "sprint" branch adds `runAcceleration × 0.2`, plus another `× 0.2` with wings. It only runs when `velocity.Y == 0 || wingsLogic > 0 || mount.CanFly()` (P:18695-18760).
  - Friction is `runSlowdown` on the ground and half of it in the air (P:18805-18831).
- **Derived jump heights** (held from the ground, flat dry ground, gravity 0.4):

| Case | jumpHeight / jumpSpeed | Apex (tiles) | Apex tick |
| --- | --- | --- | --- |
| Base | 15 / 5.01 | 6.1 | 27 |
| Balloon (`jumpBoost`) | 20 / 6.51 | 10.8 | 36 |
| Frog Leg or any +1.6 item | 15 / 6.61 | 9.0 | 31 |
| Balloon + Frog Leg | 20 / 8.11 | 14.5 | 40 |
| Water, no flipper (moves at ×0.5, P:22050) | 30 / 6.01, gravity 0.2 | about 8.2 | 60 |
| Honey (moves at ×0.25, P:22033) | 15 / 5.01, gravity 0.1 | about 3.1 | 65 |

The honey value agrees with the 3 tiles measured for the planner.

**Checked in game (Oct 21, `navigate.gear_measure`, `results/training/gear_measure.json`).** Feet rise from a flat floor, one held jump, then every extra jump started at the top of the previous one:

| Loadout | Derived here | Measured |
| --- | --- | --- |
| nothing / Shiny Red Balloon / Frog Leg | 6.1 / 10.8 / 9.0 | 6.13 / 10.75 / 9.03 |
| + Tsunami / Cloud / Sandstorm / Fart / Blizzard (extra jump, base) | 7.0 / 5.0 / 14.8 / 10.5 / 8.2 | 6.95 / 4.93 / 14.73 / 10.40 / 8.10 |
| Cloud / Blizzard / Sandstorm in a Balloon (extra jump) | 8.8 / 14.6 / 26.0 | 8.78 / 14.50 / 25.96 |
| Bundle of Balloons (all three extra jumps) | 60.2 | 59.99 |

The derivation and the measurement agree to 0.1–0.2 tiles everywhere.

---

## 2. Mechanics

### 2.1 Extra jumps

**Launch** (EJL:249-256): `velocity.Y = -Player.jumpSpeed`, `jump = (int)(Player.jumpHeight × duration multiplier)`. The extra jump then lifts like a normal held jump, so it also benefits from balloons and +1.6 items.

**Trigger** (P:19572, 19608-19611): a **fresh press of jump in the air** (`releaseJump` is required; auto-jump does not chain air jumps). Holding jump without releasing never uses an extra jump.

**Order.** The first available jump in the fixed list is used (EJL:25-37, 176-189). Each is used **once per refill**, except Flipper.

**Refill** (EJL:191-204), when any of these happens:
- standing on the ground or wall-sliding (P:25003-25006);
- jumping from the ground or a wall (P:19586-19589);
- every tick while grappled (P:20894, 21208-21217);
- a grapple-jump (P:20977).

**State.** Each jump must be re-enabled every tick by its accessory (`ResetEffects` clears it, P:17546). A jump whose accessory is removed mid-air is cancelled (EJL:228-239).

**Speed boost.** A jump's horizontal speed boost lasts only while `jump > 0`, that is until release, a head bump or landing (P:25599-25601).

| Order | Jump | Duration × | Ticks (base 15 / balloon 20) | Run speed while active | Code | Derived apex from a standstill (base / balloon) |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | Flipper | 1.0 | 15 (30 in water) | – | `FlipperJump.cs:5-31` | swimming stroke |
| 1 | Basilisk mount | 0.75 | mount jumpHeight × 0.75 | (×3 accel, ×1.5 run, but the mount override discards it) | `BasiliskMountJump.cs:9-11, 60-63` | – |
| 2 | Goat mount (WoF) | 2.0 | mount × 2 | (same, discarded) | `GoatMountJump.cs:9-11` | – |
| 3 | Santank mount | 2.0 | mount × 2 | (same, discarded) | `SantankMountJump.cs:7-9` | – |
| 4 | Unicorn mount | 2.0 | mount × 2 | (same, discarded) | `UnicornMountJump.cs:9-11` | – |
| 5 | Sandstorm | 3.0 | 45 / 60 | accel ×1.5, **maxRunSpeed ×2** | `SandstormInABottleJump.cs:7-9, 48-51` | 14.8 / 26.0 tiles |
| 6 | Blizzard | 1.5 | 22 / 30 | accel ×3, maxRunSpeed ×1.5 | `BlizzardInABottleJump.cs:7-9, 106-109` | 8.2 / 14.6 |
| 7 | Fart | 2.0 | 30 / 40 | accel ×3, maxRunSpeed ×1.75 | `FartInAJarJump.cs:9-11, 71-74` | 10.5 / 18.4 |
| 8 | Tsunami | 1.25 | 18 / 25 | accel ×1.5, maxRunSpeed ×1.25 | `TsunamiInABottleJump.cs:7-9, 95-98` | 7.0 / 12.7 |
| 9 | Cloud | 0.75 | 11 / 15 | none | `CloudInABottleJump.cs:7-9` | 5.0 / 8.8 |

- **Flipper** can start only while `wet`. Its `OnStarted` makes it available again at once, so swimming strokes are unlimited and never use up the other jumps (`FlipperJump.cs:10-31`).
- **Sandstorm** also stops the sprite from changing facing direction while it is active (P:3683-3697, 18621, 18690). Only `direction` is affected, not velocity.
- **Mount jumps** are enabled in `Mount.UpdateEffects`: Santank M:5020-5022, Unicorn M:5088, Goat M:5099, Basilisk M:5106.

**Items that grant jumps.** All lines are in `ApplyEquipFunctional` (P:12623 onward). Bottle and balloon versions of the same jump do not stack: you get one Cloud jump.

| Item | ItemID | Grants | Code |
| --- | --- | --- | --- |
| Cloud in a Bottle | 53 | Cloud | P:13114-13116 |
| Sandstorm in a Bottle | 857 | Sandstorm | P:12926-12928 |
| Blizzard in a Bottle | 987 | Blizzard | P:12935-12937 |
| Fart in a Jar | 1724 | Fart | P:12944-12946 |
| Tsunami in a Bottle | 3201 | Tsunami | P:13118-13120 |
| Cloud in a Balloon | 399 | Cloud + jumpBoost | P:13331-13334 |
| Sandstorm in a Balloon | 983 | Sandstorm + jumpBoost | P:12930-12933 |
| Blizzard in a Balloon | 1163 | Blizzard + jumpBoost | P:12939-12942 |
| Fart in a Balloon | 1863 | Fart + jumpBoost | P:12948-12951 |
| Sharkron Balloon | 3241 | Tsunami + jumpBoost | P:13016-13019 |
| Bundle of Balloons | 1164 | Cloud + Sandstorm + Blizzard + jumpBoost | P:12953-12958 |
| Bundle of Horseshoe Balloons | 5331 | Cloud + Sandstorm + Blizzard + jumpBoost + noFallDmg | P:12960-12967 |
| Blue Horseshoe Balloon | 1250 | Cloud + jumpBoost + noFallDmg | P:12969-12974 |
| Yellow Horseshoe Balloon | 1252 | Sandstorm + jumpBoost + noFallDmg | P:12976-12981 |
| White Horseshoe Balloon | 1251 | Blizzard + jumpBoost + noFallDmg | P:12983-12988 |
| Green Horseshoe Balloon | 3250 | Fart + jumpBoost + noFallDmg | P:12990-12995 |
| Pink Horseshoe Balloon | 3252 | Tsunami + jumpBoost + noFallDmg | P:12997-13002 |
| Flipper | 187 | Flipper | P:13218-13220 |
| Diving Gear | 394 | Flipper | P:13315-13317 |
| Jellyfish Diving Gear | 1860 | Flipper | P:13352-13354 |
| Arctic Diving Gear | 1861 | Flipper | P:13365-13368 |
| Frog Flipper | 3994 | Flipper (+ auto-jump, +1.6) | P:12696-12700 |
| Frog Gear | 3995 | Flipper (+ auto-jump, +1.6, wall cling) | P:12702-12707 |

Other Flipper sources:
- Flipper buff 109 (P:8550-8553);
- the mount buffs 131, 168, 265, 279 and 305 (P:9314-9425);
- Inner Tube 4404 while in water (P:11434-11436, 11654-11656);
- merman form (P:24597-24602).

### 2.2 Jump boosts and auto-jump

`UpdateJumpHeight` (P:18280-18330) runs once per tick at P:24969. On a mount, `jumpHeight` and `jumpSpeed` come from the mount instead and every boost below is skipped (P:18282-18286).

| Source | ItemID / BuffID | Effect | Code |
| --- | --- | --- | --- |
| `jumpBoost`: Shiny Red Balloon 159, Balloon Pufferfish 3225, Honey Balloon 1249, Amber Horseshoe Balloon 3251, and every balloon in 2.1 | – | **sets** `jumpHeight = 20`, `jumpSpeed = 6.51` | P:18289-18293; items P:13210-13216, 13011-13014, 13004-13009 |
| Frog Leg | 2423 | auto-jump, `jumpSpeedBoost += 1.6`, `extraFall += 10` | P:12920-12924 |
| Amphibian Boots | 3990 | same + `accRunSpeed = 6` | P:12670-12674 |
| Frog Flipper / Frog Gear / Frog Webbing | 3994 / 3995 / 3996 | same + Flipper / Flipper and wall cling / wall cling | P:12696-12713 |
| Soaring Insignia | 4989 | `jumpSpeedBoost += 1.8`, `moveSpeed += 0.075`, see 2.7 | P:12874-12878, 18294-18297 |
| Moon Lord Legs (armor) | 5001 | `jumpSpeedBoost += 1.8`, `extraFall += 10`, `jumpHeight++`, `moveSpeed += 0.25` | P:11508-11511, 18303-18308 |
| Werewolf (buff 28; Moon Charm family at night) | – | `jumpHeight += 2`, `jumpSpeed += 0.2` | P:10312-10317, 18309-18313 |
| Step Stool (in use) | 4341 (also Hand of Creation 5126) | `jumpHeight += 5`; the hitbox grows 26 px | P:12762-12765, 18314-18317 |
| (applied after all of the above) | – | `jumpSpeed += jumpSpeedBoost` | P:18318 |
| Honey block floor (`sticky`) | tile 229 | `jumpHeight /= 10`, `jumpSpeed /= 5` | P:18320-18324 |
| Dazed | buff 160 | `jumpHeight /= 5`, `jumpSpeed /= 2` | P:18325-18329 |

- **Every `jumpSpeedBoost` item gives +1.6.** They come from one family, so they don't stack in practice. No other `jumpSpeedBoost +=` exists.
- **There is no Jumping potion in vanilla.**
- **`frogLegJumpBoost`** (+2.4, P:18298-18302) is never set to true anywhere, so it is dead code.
- **Auto-jump** (P:19572; reset P:17555): holding jump starts a new ground or wall jump on landing without a release. It also refreshes extra jumps and wing time (P:19586, 25616). It does not chain air jumps, and it is turned off while wall-sliding (P:19536-19539).
- **Step Stool** triggers when you hold **up** while standing still, not mounted, with no gravity potion, no pulley and no hook (P:27460-27470).

### 2.3 Falling

**Fall damage** (P:24324-24395) runs only when `i == Main.myPlayer`.
- The safe height is `25 + extraFall` tiles.
- Damage = (tiles fallen − safe height) × 10.
- Distance is measured from `fallStart`, which is reset on every tick where any of these hold (P:24391-24394): `jump > 0`, `wet`, `slowFall`, `rocketDelay > 0`, `tongued`, or the space-gravity factor is below 0.8.

| Source | ItemID / BuffID | Effect | Code |
| --- | --- | --- | --- |
| Lucky Horseshoe | 158 | `noFallDmg` | P:13146-13148 |
| Obsidian Horseshoe | 396 | `noFallDmg` (+ fireWalk) | P:13320-13322 |
| All horseshoe balloons and the bundle | 1250, 1251, 1252, 3250, 3251, 3252, 5331 | `noFallDmg` | table 2.1 |
| **Any equipped wings**, even with no flight time left or hidden | – | no fall damage (`equippedWings != null`) | P:24365, 24375, 13710-13717 |
| Frog Leg, Amphibian Boots, frog family | – | `extraFall += 10` (safe height 35) | P:12920-12924 |
| Moon Lord Legs | 5001 | `extraFall += 10` | P:18303-18308 |
| Featherfall potion | buff 8 | `slowFall`, see below; also resets `fallStart` every tick | P:8375-8377 |
| Djinn's Curse (armor) | 3770 | `slowFall` | P:11650-11652 |
| Umbrella / Tragic Umbrella **held** | 946 / 4707 | fall speed capped at 2 unless down is held; `fallStart` reset | P:51862-51881 |
| Landing on clouds, rain clouds, snow clouds or poop blocks | tiles 189, 196, 460, 666 | no damage | P:24347-24364 |
| Flying mounts, Bunny mount, cart on track | – | no damage; other mounts × `mount.FallDamage` | P:24335-24346, 24379-24382 |
| Grappling, wall-sliding, carpet, wing glide, hover mounts | – | reset `fallStart` every tick | P:20898, 20212, 20282, 25850, M:3138 |

**Featherfall (`slowFall`)** (P:25835-25846, 26181-26203):

| Input | Gravity | Max fall speed |
| --- | --- | --- |
| Nothing held | gravity / 3 | maxFallSpeed / 3 = 3.34 |
| **Up** held | gravity / 10 | 1.0 (set to maxFallSpeed / 10 once above maxFallSpeed / 5) |
| **Down** held | normal | normal |

### 2.4 Run speed, acceleration and floors

**Boots assign `accRunSpeed`**; they do not add to it. If two boots are worn, the one in the later slot wins. `moveSpeed` bonuses add up and scale `maxRunSpeed` and `runAcceleration` only (P:24967-24968). The `accRunSpeed` sprint only works on the ground, or in the air with wings (P:18708).

| Item | ItemID | Changes | Code |
| --- | --- | --- | --- |
| Hermes Boots | 54 | `accRunSpeed = 6` | P:13122-13125 |
| Flurry Boots | 1579 | `accRunSpeed = 6` | P:13130-13133 |
| Sailfish Boots | 3200 | `accRunSpeed = 6` | P:13134-13137 |
| Dunerider Boots | 4055 | `accRunSpeed = 6`; on sand, sandstone or hardened sand: run, sprint, accel and slowdown ×1.75 | P:12692-12695, 25452-25459 |
| Amphibian Boots | 3990 | `accRunSpeed = 6` + frog jump | P:12670-12674 |
| Fairy Boots | 3993 | `accRunSpeed = 6`, `rocketBoots = 2` | P:12688-12691 |
| Spectre Boots | 405 | `accRunSpeed = 6`, `rocketBoots = 2` | P:13336-13340 |
| Hellfire Treads | 4874 | `accRunSpeed = 6`, `rocketBoots = 5` | P:13514-13518 |
| Lightning Boots | 898 | `accRunSpeed = 6.75`, `rocketBoots = 2`, `moveSpeed += 0.08` | P:13494-13499 |
| Frostspark Boots | 1862 | as Lightning + `rocketBoots = 3`, `iceSkate` | P:13500-13506 |
| Terraspark Boots | 5000 | as Frostspark + `rocketBoots = 4`, `waterWalk`, `fireWalk`, `lavaMax += 420` | P:13507-13513, 13284-13290 |
| Aglet | 285 | `moveSpeed += 0.05` | P:13231-13234 |
| Anklet of the Wind | 212 | `moveSpeed += 0.1` | P:13235-13238 |
| Panic Necklace / Sweetheart Necklace | 1290 / 1578 | when hurt (local player): Panic buff 63 for 480 ticks, `moveSpeed += 1` | P:13025-13028, 13194-13197, 37564-37567, 8512-8515 |
| Magiluminescence | 5107 | on the ground: accel ×1.75, run and sprint ×1.15, slowdown ×1.75 | P:12775-12776, 25414-25420 |
| Soaring Insignia | 4989 | accel ×1.75 always; `moveSpeed += 0.075` | P:25410-25413 |
| Shadow armor set | heads 102 / 956 | as Magiluminescence, in the air too | P:14573-14577, 25421-25427 |
| Swiftness potion | buff 3 | `moveSpeed += 0.25` | P:8355-8358 |
| Sugar Rush / Sunflower / Werewolf / Well Fed (1-3) | buffs 192 / 146 / 28 / 26, 206, 207 | +0.2 / +0.1 then ×1.1 / +0.05 / +0.2, +0.3, +0.4 | P:8340-8344, 9458-9461, 10314-10323, 10346-10375 |
| Ninja set, Adamantite melee set | – | `moveSpeed += 0.2` | P:14406-14410, 14714-14719 |
| Armor pieces with move speed (Cobalt, Orichalcum, Hallowed, Spectre, Shroomite, Solar, Vortex, Nebula, Frost, Spooky, Crystal Ninja, OOA sets …) | various | `moveSpeed += 0.05 … 0.3` | P:11495-12264 |
| Accessory prefixes Brisk / Fleeting / Hasty / Quick | prefixes 73-76 | +0.01 / 0.02 / 0.03 / 0.04 | P:12320-12336 |
| Slowing effects: Slow, Chilled, Ogre spit, Dazed, raised shield, Weak, Burning, Webbed | – | /2, ×0.75, /3, /3, /3, −0.1, ×0.5, = 0 | P:24905-24935, 10331-10336, 10177-10180, 10287-10301 |

**Flags that only change visuals.** `sailDash`, `coldDash`, `desertDash`, `fairyBoots`, `hellfireTreads` and `vanityRocketBoots` change dust and sound only (P:19071-19150, 36147-36197). They are not set while the accessory is hidden.

**Derived time to reach full sprint speed** on the ground:
- Hermes: 3 → 6 at 0.016 per tick, about 188 ticks.
- Lightning Boots: 3.24 → 6.75, about 203 ticks.

**Floors.** Floor flags are read from the tile under the player at the end of the tick (P:22464-22501). They are cleared whenever `jump > 0` or `velocity.Y != 0` (P:24500-24502). Only one floor rule applies, checked in this order (P:25432-25483):

| Floor | Tile | Without equipment | With equipment | Code |
| --- | --- | --- | --- | --- |
| Honey block (`sticky`) | 229 | run and accel ×0.25, slowdown ×2, speed clamped; jump ÷10 / ÷5 | – | P:22483, 25432-25445 |
| Asphalt (`powerrun`) | 198 | maxRunSpeed ×3.5 (about 10.5), slowdown ×2. accRunSpeed is not raised. | – | P:22486, 25446-25451 |
| Sand / sandstone / hardened sand | conversion sets | none | Dunerider Boots ×1.75 | P:22487, 25452-25459 |
| Frozen slime block (`slippy2`) | 197 | accel ×0.6, slowdown 0 | Ice Skates: accel ×2.1 total, run ×1.25 | P:22485, 25460-25469 |
| Ice (`slippy`) | 161, 162, 127, 163, 164, 200, 659 | accel ×0.7, slowdown ×0.1 (slides) | Ice Skates: accel ×2.45 total, run ×1.25, normal slowdown | P:22484, 25470-25483 |

`iceSkate` comes from:
- Ice Skates 950 (P:13206-13209)
- Arctic Diving Gear 1861 (P:13370)
- Frostspark Boots 1862 and Terraspark Boots 5000

Ice skates also stop thin ice (162) from breaking under falls faster than 7 px per tick (P:26846-26851, 21938-21965).

**Water walking** (automatic; `DryCollision` → `Collision.WaterCollision`, P:22252-22259, 22288-22296; C:3186-3230):

| Source | ID | Flag | Walks on lava |
| --- | --- | --- | --- |
| Water Walking potion | buff 15 | `waterWalk` | yes |
| Lava Waders | 908 | `waterWalk` + `fireWalk` + `lavaMax += 420` | yes |
| Terraspark Boots | 5000 | same as Lava Waders | yes |
| Water Walking Boots | 863 | `waterWalk2` | no |
| Obsidian Water Walking Boots | 907 | `waterWalk2` + `fireWalk` | no |

Code: P:8410-8413, 13271-13290.

How water walking behaves:
- You must arrive from above. Once you are `wet` you cannot climb onto the surface; water walking is only checked in dry collision.
- **Holding down passes through** (`fallThrough = controlDown`, P:26887).
- It is off when gravity is flipped (P:26485-26489).

**Fire walking.** `fireWalk` only blocks touch damage from hot tiles such as meteorite and hellstone (C:3725). It does not change movement.

### 2.5 Dashes

Triggered by a **double tap**: a fresh press of right (or left), then a second fresh press of the same direction while `dashTime` is still non-zero.
- The first press sets `dashTime = ±15`. The timer counts down by 1 per tick (`DoCommonDashHandle`, P:20120-20162).
- A fresh press uses `releaseRight` / `releaseLeft`, which are updated every tick for every player (P:26447-26462).
- **There is no dash key** in 1.4.4.
- **Air dashes work.** The dash itself is not limited to the local player.
- The dash cannot start while mounted (P:19993).

**The dash, tick by tick** (P:19882-19990):
- **Start speed** (P:20002, 20036, 20061, 20090), set once at the double tap. It is halved if a solid tile is directly ahead (P:20003-20008).
- **Decay:** while |vx| > 12 (14 for Solar), `vx *= 0.992` (0.985 for Shield of Cthulhu and Solar). While |vx| > max(accRunSpeed, maxRunSpeed), `vx *= 0.96` (0.94 for Shield of Cthulhu and Solar).
- **End:** below that, the dash ends, `vx` is **snapped** to ±max(accRunSpeed, maxRunSpeed), and the cooldown starts.
- Ground friction and air drag also apply during the dash (P:18805-18831).

| Item / set | ItemID | dashType | Start speed | Cooldown | Derived distance (ground, no boots / with Hermes / in the air) | Code |
| --- | --- | --- | --- | --- | --- | --- |
| Tabi | 977 | 1 | 16.9 | 20 | 21.7 / 19.7 / 30.5 tiles (35 / 27 / 48 ticks) | P:13167-13170 |
| Master Ninja Gear | 984 | 1 (+ wall cling 2) | 16.9 | 20 | same | P:13179-13184 |
| Shield of Cthulhu (**Expert or Master worlds only**, P:12652-12655) | 3097 | 2 | 14.5 | **30** | 11.2 / 9.5 / 14.5 tiles | P:13171-13174 |
| Solar Flare armor (needs solar shields up) | 2763-2765 | 3 | 21.9 | 20 | 26.4 / 24.8 / 33.0 tiles | P:14804-14867 |
| Crystal Assassin armor | 4982-4984 | 5 | 16.9 | 20 | as Tabi | P:14773-14778 |

The Shield of Cthulhu bounces back off an NPC it hits: `vx = −dir × 9`, `vy = −4` (P:19774-19823).

**For the bot**, the two presses must start within 14 ticks of each other: right, none, right (8 ticks apart) or right, none, none, right (12 ticks) both work. The same pattern also fires **by accident** whenever a dash item is equipped.

### 2.6 Wall climbing and sliding

| Item | ItemID | `spikedBoots` | Code |
| --- | --- | --- | --- |
| Climbing Claws | 953 | +1 | P:13155-13158 |
| Shoe Spikes | 975 | +1 | P:13159-13162 |
| Tiger Climbing Gear | 976 | +2 | P:13163-13166 |
| Master Ninja Gear | 984 | = 2 | P:13183 |
| Frog Gear | 3995 | +2 | P:12702-12707 |
| Frog Webbing | 3996 | +2 | P:12709-12714 |

Claws and Spikes together therefore make 2.

`WallslideMovement` (P:20164-20268):
- **Needs:** the player is **holding toward a wall** it touched on the previous tick (`slideDir`, set after collision at P:27019-27027), and that column is solid at the feet row and the row above (P:20178-20200). It does nothing while mounted.
- **spikedBoots ≥ 2 (cling):** while falling, `velocity.Y = −gravity + 1e-5`, so the player hangs in place (P:20203-20237). **Holding down slides at 4 px per tick** (P:20213-20215).
- **spikedBoots = 1 (slide):** when falling faster than 0.5, `velocity.Y = 0.5`, about 0.9 after gravity. Holding down gives 4 (P:20241-20251).
- **Both:** `fallStart` is reset, so a slide never causes fall damage, and `sliding = true`. Sliding refreshes extra jumps, wing time, rocket time and the carpet (P:25003-25022, 25616, 25683).
- **Wall jump** (P:19590-19606): a **fresh press of jump** while sliding. Auto-jump is disabled while sliding (P:19536-19539). It gives a normal jump plus `velocity.X = 3 × −slideDir`, a fixed 3 px per tick away from the wall. **Holding toward the wall again and repeating climbs a vertical shaft.**

### 2.7 Wings, rocket boots, flying carpet

**Wing stats.** These come from `WingStats` (`DataStructures/WingStats.cs:13-41`), which holds FlyTime, AccRunSpeedOverride, AccRunAccelerationMult and down-hover overrides. **There is no jump multiplier in `WingStats`.** The vertical behaviour is hard-coded per `wingsLogic` in `WingMovement` (P:20325-20546).

**Equipping** (P:13565-13568, 13710-13718): `wingTimeMax = FlyTime`, `wingsLogic = wingSlot`. Nothing else adds flight time in vanilla, except rocket boots and the Soaring Insignia.

Speeds, from WSI:12-20 and the ascent table at P:20364-20500:
- **Climb cap** is the maximum rise speed, a multiple of the current `jumpSpeed`. The value in brackets is for base `jumpSpeed` 5.01.
- **Run cap** is `AccRunSpeedOverride`, applied in the air (P:27575-27580).
- **Hover** means holding down and jump; it is described after the table.
- **Climb** is the derived maximum height when holding jump from the ground, all flight time spent climbing, from a base jump. "+RB" adds rocket boots (+42 ticks, see below).

| Slot | Wings (ItemID) | FlyTime | Climb cap × jumpSpeed (px/tick) | Run cap | Accel × | Hover | Derived climb (tiles) / +RB | WSI |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 46 | Fledgling Wings (4978) | 25 | 1.5 (7.5) | 3 | 1 | – | 18 / 38 | 29 |
| 1, 2 | Demon Wings (492), Angel Wings (493) | 100 | 1.5 (7.5) | 6.25 | 1 | – | 53 / 73 | 30-31 |
| 6, 7, 10, 25 | Fairy (761), Harpy (785), Frozen (822), Fin (2494) | 130 | 1.5 (7.5) | 6.75 | 1 | – | 67 / 87 | 32-35 |
| 4 | Jetpack (748) | 150 | 1.5; holding **up**: 3 (15), at 2 flight time per tick | 6.5 | 1 | – | 77 / 96 | 36 |
| 5, 9, 13, 14, 15 | Butterfly (749), Flame (821), Leaf (1162), Bat (1165), Bee (1515) | 160 | 1.5 (7.5) | 7.5 | 1 | – | 81 / 101 | 37-41 |
| 8, 11, 24, 27 | Bone (786), Spectre (823), Beetle (2280), Mothron (2770) | 170 | 1.66 (8.3) | 7.5 | 1 | – | 94 / 116 | 42-45 |
| 22 | Hoverboard (1866) | 170 | 1.66 (8.3) | 6.5 | 1 | yes, run 10 | 94 / 116 | 46 |
| 12, 20, 21, 23 | Steampunk (948), Tattered Fairy (1797), Spooky (1830), Festive (1871) | 180 | 1.805 (9.0) | 7.5 | 1 | – | 107 / 130 | 47-50 |
| 26 | Fishron Wings (2609); also `ignoreWater` (P:13569-13572) | 180 | 2.5 (12.5) | 8 | 2 | – | 143 / 175 | 51 |
| 37 | Betsy's Wings (3883) | 150 | 2.5 (12.5) | 7 | 2.5 | yes, run 12 | 119 / 152 | 53 |
| 44 | Empress Wings (4823) | 150 | 2.75 (13.8) | 8 | 2 | – | 128 / 164 | 54 |
| 45 | Celestial Starboard (4954) | 180 | 4.5 (22.5) | 8 | 4.5 | yes, run 16 | 201 / 260 | 52 |
| 29, 32 | Solar Wings (3468), Stardust Wings (3471) | 180 | 3 (15) | 9 | 2.5 | no (down + jump only flies up) | 167 / 206 | 56-57 |
| 30, 31 | Vortex Booster (3469), Nebula Mantle (3470) | 180 | 2.45 (12.3) | 6.5 | 1.5 | yes, run 12 (Nebula: no forced flap) | 142 / 175 | 58-59 |
| 3, 16-19, 28, 33-36, 38-43 | Developer wings: Red's (665), D-Town's (1583), Will's (1584), Crowno's (1585), Cenx's (1586), Lazure's Barrier Platform (3228), Yoraiz0r's Spell (3580), Jim's (3582), Skiphs' Paws (3588), Loki's (3592), Arkhalis' (3924), Leinfors' (3928), Ghostar's (4730), Safeman's (4746), FoodBarbarian's (4750), Grox The Great's (4754) | 150 | 1.5 (7.5) | 7 | 1 | slots 28, 33, 35 hover at normal speed | 77 / 96 | 60 |

**Flight trigger** (P:25621): `controlJump && wingTime > 0 && jump == 0 && velocity.Y != 0`.
- **Hold jump while airborne**, with no new press needed. Holding jump from the ground flies as soon as the 15-tick jump runs out.
- **An extra jump is used first** if jump is pressed fresh in the air and one is available (it sets `jump > 0`). Wings take over when it ends.

**Climb per flap tick** (default wings, P:20502-20517):
- `vy −= 0.1`, plus another −0.5 if falling or −0.1 if `vy > −0.5 × jumpSpeed`;
- capped at −climb cap × jumpSpeed;
- **no gravity** on flap ticks;
- each flap costs 1 flight time (P:20539).

**Hover** (wings 22, 28, 30, 31, 33, 35, 37, 45):
- Hold **down + jump** (P:25625, 26216-26236).
- The player holds altitude: `vy *= 0.9` and snaps to 1e-5. The run cap becomes the hover speed (P:27564-27572).
- Hovering without left/right input costs 0.5 flight time per tick (P:20533-20536).

**Glide.** With no flight time left, holding jump while falling gives gravity / 3 and a maximum fall of 3.34 px per tick, and resets `fallStart` (P:25847-26162).

**Refill** (P:25616-25620): `wingTime = wingTimeMax` when **on the ground (or wall-sliding) with jump released**, or on an auto-jump. It also refills every tick while grappled (P:21208-21217).

**Soaring Insignia** (4989): while wings still have any time left, `wingTime` is reset to max after each flap (P:20542-20545), which means **infinite flight**. It also refills rocket time every tick (P:25687-25690) and adds jumpSpeedBoost +1.8.

**Rocket boots:**
- Sources (equip lines in 2.4): Rocket Boots 128 (P:13138-13141), Spectre 405, Lightning 898, Fairy 3993, Frostspark 1862, Terraspark 5000, Hellfire Treads 4874.
- **Every pair has `rocketTimeMax = 7`.** It is a field initialiser (P:3096) that nothing else changes. The `rocketBoots` number only changes the sound and dust rate (P:25697-25719).
- **Trigger** (P:25691): `(wingTime == 0 || no wings) && controlJump && rocketDelay == 0 && canRocket && rocketRelease && !AnyExtraJumpUsable()`.
  - You must **release jump after the jump and press it again**, with **all extra jumps used**.
  - `canRocket` needs `vy > −jumpSpeed` (P:25611-25614).
  - `rocketRelease` is set only by releasing jump (P:19619).
- **Each charge** is 10 ticks of thrust (P:25693-25696), which keeps going even if jump is released. That is 7 charges, about 70 ticks in total.
- **Thrust per tick** (P:25739-25779): `vy −= 0.1`, plus −0.5 if falling or −0.1 if `vy > −0.5 × jumpSpeed`, capped at −1.5 × jumpSpeed, with no gravity. Derived: about 39 tiles of climb from a held base jump, without the release gap.
- **Refill:** on the ground, wall-sliding, auto-jump (P:25683-25686), grapple or pulley.
- **With wings**, the 7 charges become 42 ticks of wing time once airborne: `wingTime += rocketTime × 6`, capped at `wingTimeMax + 42` (P:25652-25662). The rockets themselves never fire while the wings still have time.
- **In water with any Flipper**, `AnyExtraJumpUsable()` is always true, so **rocket boots never fire** there.

**Flying Carpet** (934, `carpet`, P:13151-13153; `CarpetMovement` P:20270-20320):
- **Trigger:** holding jump in the air once everything else is used up: no extra jump left, `jump == 0`, `rocketTime == 0`, `wingTime == 0`, not grappled, not mounted.
- Gives 300 ticks of level flight: `vy` is set to just cancel gravity.
- While it flies, `maxRunSpeed ×1.5` and accel ×1.25 (P:25484-25488).
- Refills on the ground or wall slide (P:25016-25022). It does not work with gravity flipped.

### 2.8 Liquids

How liquid moves you:
- **Gravity and jump values** in liquids are in 1.1.
- **Displacement:** each tick the player moves only a fraction of its velocity (P:26968-26982):

  | Liquid | Fraction moved | Code |
  | --- | --- | --- |
  | Water and lava | 0.5 | P:22050-22064 |
  | Honey | 0.25 | P:22033-22048 |
  | Shimmer | 0.375 | P:22017 |

- **Run speed** has no separate liquid multiplier; the displacement fraction is the only liquid effect on it.
- **`ignoreWater`** skips the displacement slowdown only; liquid gravity and jump values still apply.
- **Leaving a liquid** cuts the remaining jump to a fifth (P:26688-26691).
- **Liquid flags** come from the previous tick (P:26562-26582).
- **Swimming without a flipper:** a jump needs standing ground or a wall slide.

| Item / buff | ID | Effect | Triggered by | Code |
| --- | --- | --- | --- | --- |
| Flipper and the items in 2.1 (Diving Gear 394, Jellyfish 1860, Arctic 1861, Frog Flipper 3994, Frog Gear 3995) | – | Flipper jump: unlimited swim strokes, each `vy = −6.01` for 30 ticks (water values) | a **fresh press** of jump while wet; repeat to keep rising | `FlipperJump.cs`; P:3770-3789 |
| Flipper potion | buff 109 | Flipper + `ignoreWater` | as above | P:8550-8553 |
| Neptune's Shell / Moon Shell / Celestial Shell | 497 / 861 / 3110 | merman form in water: gravity 0.3, max fall 7, no water slowdown. **Holding jump never runs out** (`jump` is not counted down), so you rise at 5.01 for as long as jump is held. No drowning. | automatic in water; hold jump | P:13680-13683, 13251-13253, 13519-13521, 24596-24608, 19559-19569 |
| Fishron Wings | 2609 | `ignoreWater` | automatic | P:13569-13572 |
| Inner Tube | 4404 | floats at the surface; Flipper while wet | automatic | P:11654-11656, 11434-11436, 22099-22147 |
| Diving Helmet / diving gears | 268, 394, 1860, 1861 | breath lasts ×6 longer (not movement) | automatic | P:4519-4533, 11638-11641 |
| Gills potion | buff 4 | no drowning | automatic | P:8359-8362 |
| Lava Charm, Molten Charm, Lava Skull, Molten Skull Rose, Lava Waders, Terraspark | 906, 4038, 3999, 4003, 908, 5000 | `lavaMax += 420`: 7 s of lava immunity | automatic | P:13284-13298 |
| Obsidian Skin potion | buff 1 | `lavaImmune` | automatic | P:8319-8322 |
| Chromatic Cloak | 5355 | immune to the shimmer "phase" unless down is held | hold down to phase | P:13642-13645 |

**Shimmer contact** (local player, above the underworld) gives buff 353 (P:26565-26576). The player is frozen and falls through blocks with no control (P:10201-10219). Avoid it.

### 2.9 Grappling hooks

**Firing** (`QuickGrapple`, P:6910):
- **Only for `whoAmI == Main.myPlayer`**, on a fresh press of the hook control (`controlHook && releaseHook`, P:26290-26303).
- **The item** is the hook slot (`miscEquips[4]`) or the first hook in the inventory (P:7187-7206).
- **Aim** is always the **mouse**: `Main.mouseX + Main.screenPosition.X − player center` (P:7163-7177). It fires along `direction` if the mouse is exactly on the player. The hook flies straight at `shootSpeed`, with no gravity (Pr:55696-55888).
- **Firing dismounts** you (P:6944-6947).
- **A new hook cannot be fired** while the previous one of the same type is still flying out (P:7041-7051).

**Latching** (Pr:55722-55790, 55971-55977):
- A hook latches on any `Main.tileSolid` tile, **platforms included**, or on minecart track.
- The Squirrel Hook also latches tree trunks.
- Hooks don't latch on walls, ropes, or (except the Squirrel Hook) trees.
- Hooks retract when they pass their reach (Pr:55698-55717), and die beyond 2500 px.
- **Snapping to the tile and changing to "latched" only happens when `Main.myPlayer == owner`** (Pr:55800-55803).

**Pull** (`GrappleMovement`, P:20858-20985; `GetGrapplingForces`, P:21096-21206):
- Each tick the player's velocity is set toward the average of the latched hooks, capped at the pull speed. Normal movement, jumps and gravity are off while grappled (P:25404).
- **Every tick while grappled:** `fallStart` is reset, and wing time, rocket time and extra jumps are refilled (P:20894-20898).

**Release** is a **fresh press of jump** (P:20962-20984).
- If the player is standing (`vy == 0`) and not holding down, it also jumps: `vy = −jumpSpeed`, `jump = jumpHeight / 2`.
- Otherwise there is no impulse.
- Either way all hooks are removed and the extra jumps are refreshed.
- Pressing hook again does not release; single-hook items fire a new hook instead (P:7066-7077).

**Max latched hooks.** On latch, the oldest hook beyond this number is killed (Pr:55819-55856).

| Hook | ItemID | Launch speed | Reach px (tiles) | Retract speed | Pull speed | Max latched | Code (shootSpeed) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Grappling Hook | 84 | 11.5 | 300 (18.8) | 11 | 11 | 1 | I:4626 |
| Amethyst, Topaz, Sapphire, Emerald, Ruby, Diamond Hook | 1236-1241 | 10, 10.5, 11, 11.5, 12, 12.5 | 300, 330, 360, 390, 420, 450 | 11 … 14.75 | 11 | 1 | I:18351-18431; Pr:55702-55708, 55908-55911 |
| Amber Hook | 4257 | 12.5 | 420 (26.3) | 15 | 11 | 1 | I:41006 |
| Squirrel Hook (also tree trunks) | 4759 | 11.5 | 300 (18.8) | 11 | 11 | 1 | I:43706 |
| Candy Cane Hook | 1915 | 11.5 | 400 (25) | 11 | 11 | 1 | I:24080 |
| Bat Hook | 1800 | 13.5 | 500 (31.3) | 20 | **14** | 1 | I:23242; P:21189-21192 |
| Ivy Whip | 185 | 13 | 400 (25) | 15 | 11 | 3 | I:5960 |
| Slime Hook | 2585 | 13 | 300 (18.8) | 11 | 11 | 3 | I:29076 |
| Fish Hook | 2360 | 13 | 400 (25) | 11 | 11 | 2 | I:27389 |
| Skeletron Hand | 1273 | 15 | 350 (21.9) | 11 | 11 | 2 | I:18876 |
| Dual Hook | 437 | 14 | 440 (27.5) | 17 | 11 | 1 (the two colours alternate) | I:8961 |
| Web Slinger | 939 | 10 | 375 (23.4) | 11 | 11 | 8 | I:15097 |
| Spooky Hook | 1829 | 15.5 | 550 (34.4) | 22 | 11 | 3 | I:23554 |
| Christmas Hook | 1916 | 15.5 | 550 (34.4) | 17 | 11 | 3 | I:24096 |
| Tendon, Thorn, Illuminant, Worm Hook | 3020-3023 | 15 | 480 (30) | 18 | 11 (Thorn **12**) | 3 | I:31853; P:21193-21196 |
| Anti-Gravity Hook | 2800 | 14 | 500 (31.3) | 20 | 11 | 3 | I:30552 |
| Lunar Hook | 3572 | 18 | 550 (34.4) | 24 | **16** | 4 | I:36405; P:21197-21200 |
| Static Hook | 3623 | 16 | 600 (37.5) | 24 | ≤ 6 (see below) | 1 | I:36832 |
| Hook of Dissonance (`QueenSlimeHook`) | 4980 | 4 × 4 updates = 16 | 500 (31.3) | 44 | teleports | 1 | I:44727; Pr:9307 |

Special hooks:
- **Anti-Gravity Hook:** free 2D movement, holding about 200 px from the hook, steered with left, right, up and down (P:21125-21141).
- **Static Hook:** a fixed rope length. Up, down, left and right move you along the rope at up to 6 px per tick (P:21142-21159).
- **Hook of Dissonance:** teleports you to the hook on latch (P:20987-21030, Pr:55873-55880).

### 2.10 Gravity and other buffs

| Source | ID | Effect | Triggered by | Code |
| --- | --- | --- | --- | --- |
| Gravitation potion | buff 18 → `gravControl` | flips gravity | a **fresh press of up**; not while mounted | P:8423-8425, 25540-25558 |
| Gravity Globe | 1131 → `gravControl2` | same | same | P:13185-13187, 25560-25579 |

What a flip does:
- It sets `gravDir = ±1`, resets `fallStart` and cancels the jump.
- Without either source, gravity is forced back to normal (P:25580-25583).
- While flipped: water walking is off, the Flying Carpet doesn't work, and the hook aim is mirrored.

The other buffs that change movement are listed where they act:

| Buff | Section |
| --- | --- |
| Featherfall | 2.3 |
| Swiftness, Panic, Sugar Rush, Well Fed | 2.4 |
| Water Walking | 2.4 |
| Flipper, Gills, Obsidian Skin | 2.8 |
| Werewolf | 2.2 |
| Webbed, Dazed, Slow, Chilled | 2.2, 2.4 |

### 2.11 Mounts

**Toggling:**
- **Mount key:** `QuickMount` (P:6676-6707) runs on `controlMount && releaseMount`, inside the local-player input block, **before** `SetControls` (P:23778-23789). It uses the mount slot `miscEquips[3]` or the first mount item in the inventory, and needs head room (P:6709-6718). It is refused if gravity is flipped.
- **Using a mount item** also works (P:42596-42599).
- **Buff:** adding a mount buff mounts any player, with no space check (P:8325-8330, 9294-9437).
- **Dismount:**
  - pressing the mount key again;
  - firing a hook;
  - being frozen, webbed or stoned;
  - Bee or UFO mounts entering water (local player only, P:26793-26800).
- **Jump does not dismount.**

**On a mount:**
- `maxRunSpeed = RunSpeed`, `accRunSpeed = DashSpeed` and `runAcceleration = Acceleration` replace everything else, and wings and rocket boots are switched off (P:25494-25503).
- Jump values come from the mount (P:18282-18286).
- **Mounts with `BlockExtraJumps`** consume all extra jumps (P:24999-25002).

**Flying mounts** (Rudolph, Pigron, Gelatinous Pillion):
- Hold jump in the air while flight time lasts (P:25786-25833; `Mount.Flight`, M:2571-2579).
- Climb is `vy −= 0.5` if falling, else −0.1, capped at −1.5 × jumpSpeed. Holding down slows the fall.

**Hover mounts** (`usesHover`, M:3047-3138):
- Lift off with a **fresh press of up** on the ground, or by jumping (P:25584-25587).
- In the air, **up or jump** rises at up to −2 − 6 × (1 − fatigue), **down** sinks (up to 8 px per tick), and **nothing held** holds altitude on mounts that ignore fatigue.
- `fallStart` is reset every tick.

Columns: Run is `RunSpeed`, Dash is `DashSpeed`, Acc is `Acceleration`, jH / jS are jump height (ticks) and jump speed.

| Mount (MountID) | Item (ItemID) | Run | Dash | Acc | jH / jS | Flight | Notes | Code (M) |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Rudolph (0) | Reindeer Bells (1914) | 5.5 | 12 | 0.09 | 17 / 5.31, more when fast | 160 ticks, more when fast | flies | 648-658, 2413-2442, 5402-5408 |
| Bunny (1) | Fuzzy Carrot (2428) | 4 | 7.8 | 0.13 | 15 / 5.01, more when fast | – | no fall damage | 756-764 |
| Pigron (2) | Scaly Truffle (2429) | 5 | 9 | 0.08 | 10 / 6.01 | 160 | flies | 703-710 |
| Slime (3) | Slimy Saddle (2430) | 4 | 4 | 0.18 | 12 / 8.25 | – | auto-jump; bounces off enemies; fall damage ×0.5 | 811-820 |
| Turtle (4) | Hardy Saddle (2491) | 2 | 5 | 0.08 | 12 / 3.7 | – | swims at 10 | 1064-1073 |
| Bee (5) | Honeyed Goggles (2502) | 2 (flying up to 6) | 2 | 0.16 | 10 / 4 | 320, with fatigue | hover; no extra jumps; no fall damage | 1116-1127 |
| Minecart (6), other carts | Minecart (2343) etc. | 13 | 13 | 0.04 | 15 / 5.15 | – | rails only | 861-876, 2344-2360 |
| UFO (7) | Cosmic Car Key (2769) | 8 | 8 | 0.16 | 10 / 4 | **infinite** | hover; no extra jumps | 1172-1183, 3213-3219 |
| Drill (8) | Drill Containment Unit (2768) | 6 | 4 | 0.16 | 10 / 4 | infinite | hover; mines toward the mouse | 1226-1238 |
| Scutlix (9) | Brain Scrambler (2771) | 8 | 8 | 0.4 | 22 / 10.01 | – | high jump | 1296-1309 |
| Unicorn (10) | Blessed Apple (3260) | 4 | 12 | 0.3 | 10 / 8.01 | – | extra jump ×2; fall ×0.2 | 1378-1386, 5088 |
| Cute Fishron (12) | Shrimpy Truffle (3367) | 2 (flying 15.5, swimming 16) | 1 | 0.2 | 4 / 3 | infinite | hover; half speed except in water, rain or blood moon | 1493-1505; P:4707-4721, 25504-25508 |
| Basilisk (14) | Ancient Horn (3771) | 8 | – | 0.25 | 20 / 8.01 | – | extra jump ×0.75; fall ×0.2 | 1602-1609, 5106 |
| Golf Cart (17) | Golf Cart Keys (4264) | 8 | – | 0.25 | 20 / 8.01 | – | – | 1652-1659 |
| Witch's Broom (23) | Witch's Broom (4444) | 9 | 9 | 0.16 | 10 / 4 | infinite | hover; no extra jumps | 1700-1711 |
| Flamingo (37) | Mollusk Whistle (4716) | 6 | – | 0.15 | 14 / 6.01 | – | Flipper | 1754-1761 |
| Horses (40-42) | 4785-4787 | 3 | 9 | 0.25 | 6 / 7.01 | – | fall ×0.5 | 2269-2277 |
| Pogo Stick (43) | Pogo Stick (4791) | 5 | – | 0.1 | 8 / 8 | – | auto-jump | 1810-1818 |
| Pirate Ship (44) | The Black Spot (4792) | 3 (flying 7) | 6 | 0.12 | 3 / 1 | infinite | hover | 1858-1870 |
| Spooky Wood (45) | Hexxed Branch (4793) | 12 | 16 | 0.5 | 14 / 7 | – | fastest ground mount; fall ×0.1 | 1897-1906 |
| Santank (46) | Toy Tank (4794) | 8 | 8 | 0.4 | 8 / 9.01 | – | extra jump ×2 | 1944-1957, 5022 |
| Goat (47) | Goat Skull (4795) | 4 | 12 | 0.3 | 10 / 8.01 | – | extra jump ×2 | 1998-2006, 5099 |
| Dark Mage's Tome (48) | 4796 | 8 | 8 | 0.2 | 5 / 6 | holds altitude, cannot rise | – | 2059-2069, 3072-3091 |
| Lava Shark (49) | Superheated Blood (4828) | 2 | 1 | 0.4 | 4 / 3 | hovers only in liquid | lava immune | 2102-2114 |
| Queen Slime (50) | Gelatinous Pillion (4981) | 5.5 (flying +2) | 5.5 | 0.2 | 10 / 7.25 | 160, climbs at half speed | auto-jump | 2151-2160 |
| Wolf (52) | Lilith's Necklace (5130) | 9.5 | – | 0.18 | 18 / 9.01 | – | fall ×0.1 | 2206-2212 |

---

## 3. What the bot can use, and what the planner must model

| Mechanic | Usable with the existing three heads? | How | The planner must model |
| --- | --- | --- | --- |
| Run speed, accel, moveSpeed (boots, Aglet, Anklet, Swiftness, armor) | **yes**, automatic | hold a direction; full sprint takes about 190 ticks of run-up on the ground | max run speed and sprint cap, acceleration, run-up distance; sprint holds in the air but only grows on the ground or with wings |
| Floors (ice, asphalt, honey block, sand with Dunerider Boots) | **yes**, automatic | – | per-floor speed multipliers; slide distance on ice |
| Water walking | **yes**, automatic | **down** drops through | liquid surfaces as floors when arriving from above; lava only with `waterWalk` |
| Jump boosts (balloons, +1.6 frog family, Soaring Insignia) | **yes**, automatic | hold jump | jumpHeight and jumpSpeed → jump apex and arc (table 1.3) |
| Auto-jump | **yes** | hold jump through landing | chained ground jumps |
| Extra jumps | **needs timing only** | jump **no** for one decision, then **yes**, while airborne; each jump once per refill | number and order of the jumps, the height of each (table 2.1), Sandstorm's ×2 run speed, refill on ground, wall slide or grapple |
| Fall protection (horseshoe, wings, Featherfall, frog +10) | **yes**, automatic | – | safe drop height: 25, 35, or unlimited |
| Featherfall | **yes** | **up** falls at 1 px per tick, **down** falls normally | fall speed by vertical input → horizontal glide reach |
| Gravity flip (potion, Gravity Globe) | **yes, but risky** | any fresh **up** press flips | ceilings become floors; must not be enabled by accident |
| Dash | **needs timing only** | right, none, right (or right, none, none, right) | dash distance (about 22 tiles for Tabi on the ground), 20 or 30 tick cooldown; Shield of Cthulhu needs Expert |
| Wall cling / slide / wall jump | **yes** | hold toward the wall; **down** slides fast; fresh jump press to jump off, then steer back | vertical shafts climbable with spikedBoots ≥ 1; extra jump and wing refills on walls |
| Wings | **yes** | hold jump in the air; **down + jump** hovers on hover wings; on the ground release jump once to refill | flight time, climb rate and cap (× jumpSpeed), air run cap, glide at 3.34 px per tick fall, refill rule |
| Rocket boots | **needs timing only** | after the jump and all extra jumps: release jump, then press | 7 × 10 ticks of thrust; converted to 42 wing ticks when wings are worn |
| Soaring Insignia | **yes** | as wings | infinite flight once airborne with wings |
| Flying Carpet | **yes** | keep holding jump after everything else is spent | 300 ticks of level flight at ×1.5 run speed |
| Flippers | **needs timing only** | repeated fresh jump presses in water | unlimited swim strokes; up to 8 tiles per stroke in water (derived) |
| Merman (Neptune's Shell family) | **yes** | hold jump in water | unlimited rise in water; no drowning |
| Step Stool | **yes, but accidental** | **up** while standing still | +26 px reach and +5 jump ticks |
| Umbrella | **needs a new action** (select a held item) | – | 2 px per tick fall cap |
| Grappling hooks | **needs a new action**: hook trigger + 2D aim | release with a fresh **jump** press (existing head) | reach, latchable tiles (solid tiles and platforms), pull speed, refills while hooked |
| Mounts | **needs a new action**: mount toggle | movement on the mount uses the existing heads (hover: up, down, jump) | mount run, jump and flight stats replace the player's |

---

## 4. Traps in the code

1. **Static fields shared by all players.**
   - `Player.jumpHeight`, `Player.jumpSpeed` and `Player.defaultGravity` are `static` (P:2876-2883). Each player's `Update` rewrites them (P:23349-23350, 24969). A planner, a `ModSystem` or another bot that reads them gets **whichever player updated last**.
   - The extra-jump launch (EJL:253-254), the grapple release jump (P:20969) and every wing and rocket cap (multiples of `jumpSpeed`) read these statics inside the current player's update. That is correct there, but wrong if read anywhere else.
   - `Player.Settings.HoverControl` (P:365) is static too; it is a local client setting.
   - Read per-player values only inside that player's update, or compute them from equipment.
2. **Code that only runs for `Main.myPlayer`.**
   - The control input block (P:23670-24323), `QuickMount`, `QuickGrapple` (P:26290), fall damage (P:24322), lava damage (P:26502), drowning (P:21758), the shimmer buff (P:26568), cobweb breaking (P:21308) and thin-ice breaking (P:26846).
   - The second half of `ApplyEquipFunctional` (P:13720), which has no movement effects.
   - **Hook latching** (Pr:55800).
   - Our mod sets `Main.myPlayer = bot` around `bot.Update` (BMS:210-233) and around the bot's projectile updates (BMS:142-153), so bots get all of these, including fall damage and latching hooks. **Keep both swaps.** Any new code that calls `QuickGrapple`, `QuickMount` or hook logic outside those windows must swap `Main.myPlayer` too.
3. **Bot controls arrive at `SetControls` (P:23959). Some checks run before that.**
   - **The mount key is checked earlier** (P:23778), and `ResetControls` (P:27349-27373) clears `controlMount` before the next check. So setting `controlMount` in `SetControls` never mounts. A mount action must call `player.QuickMount()` (with `Main.myPlayer` swapped) or set the mount buff.
   - **The hook key is checked later** (P:26290), so `controlHook` from `SetControls` works. `BotData` already has `ControlHook` and `AimDirection`. BMS:212-219 converts the aim into `Main.mouseX/Y`, which `QuickGrapple` reads (P:7163-7171).
   - **Armor double-tap bonuses** (`KeyDoubleTap`, P:23906-23957; Vortex stealth, Forbidden storm) read the controls before `SetControls`, so they never fire for bots.
   - **Dashes use a different detector** (`DoCommonDashHandle`, P:20120) that runs after `SetControls`, so they do work.
4. **Values overwritten mid-tick.**
   - `accRunSpeed` is assigned by boots (the last slot wins), raised by wings in the air (P:27575), and replaced by mounts (P:25502).
   - `gravity` is divided by Featherfall (P:25839-25845), so `player.gravity` read after the update is the reduced value with the sign of `gravDir`.
   - Mounts set `wings`, `wingsLogic` and `rocketBoots` to 0 (P:25495-25500).
   - Extra-jump speed multipliers from mount jumps are thrown away by the mount override (P:25483 vs 25501).
5. **One-tick lag.**
   - Floor flags (P:27047) and wall contact (`slideDir`, P:27019) are set after collision and used on the next tick.
   - Liquid flags (P:26562) work the same way.
   - Hooks register in `grappling[]` during projectile updates, after all players, so a player sees the previous tick's hook state (P:27079-27080).
   - The floor flags are also cleared whenever airborne (P:24500).
6. **Wings refill only on the ground with jump released** (P:25616). A policy that holds jump through a landing does not get its flight time back. Rocket boots refill without this condition (P:25683).
7. **Extra jumps refill on walls and while hooked.** With wall-cling gear, a wall resets every jump, wing and rocket. With Flippers in water, `AnyExtraJumpUsable()` is always true, which blocks rocket boots (P:25691).
8. **Balloons make jumps in water shorter.** `jumpBoost` assigns 20 / 6.51, overwriting the water values 30 / 6.01 (P:18289-18293 vs P:23407-23408).
9. **Expert-only and master-only accessories do nothing in Classic worlds** (P:12652-12655). This includes the Shield of Cthulhu.
10. **Gravity potions flip on every fresh up press** (P:25540). The Step Stool rises on up while standing still (P:27460). Both turn the bot's "up" action into something else, so keep them off unless the observation shows them.
11. **Dead code and wrong documentation.**
    - `frogLegJumpBoost` (+2.4) is never set (P:18298).
    - The doc comments in Player.cs disagree with the code in three places:

      | Topic | Doc comment says | Code does |
      | --- | --- | --- |
      | Tabi cooldown | 30 (P:1273) | 20 (P:19889) |
      | Ice Skates on ice | ×2.1 accel (P:2906) | ×2.45 (×2.1 is frozen slime) |
      | Asphalt | "triples" speed | maxRunSpeed ×3.5 |

12. **Visual code that also holds state.** The `sandStorm` flag is set from the Sandstorm jump's visuals code (`SandstormInABottleJump.cs:39`, read at P:3683-3697). It only freezes the facing `direction`, so it does not change movement. The boot "dash" flags in 2.4 are not set while the accessory is hidden (P:12660-12664), and they are cosmetic too.
