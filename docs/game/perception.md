# Perception and fleeing

Original sense/GroupSense/vision/hearing/runaway research. Binary literals are Verified;
interpreted flags/control flow stay Observed or Unknown. AI target choice: [ai](ai.md);
relations: [factions](factions-squads-towns.md). Simulation senses/leash remain approximations.

## Sight

- **Range** (`FUN_140851bc0`, **Verified** literals): 500 inside a building, else 1500 x race `vision range mult`; x
  lerp(daylight, 0.5, 1); x weather: lerp(s(1-P), 1, 0.4) dust, lerp(.., 1, 0.75) any other affecting weather on the looker
  ([weather](../formats/weather.md#effects-on-characters), **Verified**; Meitou `AggroSystem.RangeOf`).
- **Cone**: v = (cos a - 0.2) / 0.8, zero past 78.46 deg. Crawling/ground posture x0.5; sneaking x0.75 (v >= 0.5) or x0.5,
  then x lerp(stealth, 1, 0.6). Stealth scales skill x carry 0.75 x crawl 2 x load/equipment/race/injury, clamped 0..1.
- **Seen** (`FUN_140852f70`, **Verified** literals): d <= lerp(ambient, F, 1) x contrast x R x v, R x2 when the looker is on
  top (below); contrast = clamp(target light - looker light + 1, 0, 2), ambient = clamp(mean of both, 0, 1); F = 0.2 when the
  looker is inside a building (character vfunc +0x78, the building it is in; **Observed**), else 0.4. v must exceed 0.01.
- **Place** (**Observed**, `FUN_140857b10`, Character slots 59/60): each character has a location (slot 59: a carried one its
  carrier's, a caged or bedded one its building's, else its own handle `+0x80`, whose writer is not traced; type 0xb is
  outdoors). Looker and target must share it unless either is **on top**: slot 60, in a building and `+0xd5` set, from an
  upward physics query (mask `0x8079e00`, in the character update) finding nothing over it, i.e. a roof or roofless part.
  A building whose BUILDING `enforce ceiling` is set (7) has nobody on top above floor 0 (`Character::threadedUpdatePeriodic` `FUN_1405d00b0`, **Verified**).
  The location compare (vfunc +0x10) is read as "same building". A physics ray (`FUN_1408533a0`, start 18 above the looker,
  blocked by anything more than 2 short of the target) runs in some branches.
- **Light level** (`+0xd8`, `FUN_140a0af10`, `FUN_140a08da0`, **Verified** literals): set each update to the daylight factor;
  when requested by a sight test (`+0xdc`) and the character is the player's or daylight < 0.75, it is resampled: 1 above
  daylight 0.5, else 2 x daylight + per light within 500 of the zone search: 1.5 x power scale x (1 - d^2 / r^2) (x the spot
  cone (cos - cos(outer/2)) / (cos(inner/2) - cos(outer/2)), clamped) while d <= r; a light counts only when its indoor flag
  equals the character's and, both inside, its floor (custom parameter w) equals the character's (slot 0x60). Building
  lights and characters' carried lights both count.

Meitou (`SightMap`): every sight test (aggro, dialogue sightings, crime witnesses, bounty spotting, slave catching, loot witnesses) uses these
rules. Rooms are the registered towns' buildings' `interior mask` volumes and lamps their lights, plus the lanterns characters carry
(`CharacterAppearance.Lights` at the owner, its place and floor) and the lamps of completed player buildings (outdoor lights; ruins and the player's buildings have no room); a lit lamp of another building's interior is not counted indoors (floors are
compared only within the character's building). With the `interiorlamps` switch on (Meitou, default) interior lamps count at
any daylight, as the renderer draws them; off, night-only ones count at daylight <= 0.1 as in the game. No physics ray, no weather, no posture factor; the 0.75 / 0.5 light gate is
evaluated every tick for everyone. The overhead and floor queries are redone only when a character moves (headless, Release:
0.29 ms per tick for 840 characters, 540 indoors, standing). Observed in the data (headless probe of five big towns' 60
lit rooms): standing under an interior lamp gives light 0.7-2.9, 444 of 462 lamps lighting the floor below them; the Hub's
intact buildings are mostly ruins, which count as outdoors.

Can-see first checks state/squad-shared sightings (within2R, no cone/light), then player rules,
building compatibility, NPC test/stealth accumulation and conditional physics ray. Ray starts
18 above looker, obstruction>2 short of target blocks. Unknown flags/range cap/special tests are
not universal vision rules. Shared senses keep targets about two completed squad update rounds.
Faction hostility/disguise/personal tags choose enemy; coexistence not read in this predicate.

## Spotting a sneaker

A sneaking target in view (v > 0.01) of an NPC is not seen at once (`FUN_140857b10` -> `FUN_140856cb0`, extra args 0.5
and stealth x 5). Each looker keeps a meter per target (`SensoryData::SpottingPeopleMgr::Spot`, map at sense +0x150).
**Verified** literals, flow **Observed**:

- In view with line of sight: the meter fills by dt / T, T = lerp(v, 0.5 + 5 s, 0.5) x lerp(f, 4, 1). f is the looker's
  alertness: 0.5 without a current goal, else a goal float (+0x3c, meaning **Unknown**); 0 when it is KO'd, busy
  (+0x2f8 count > 0) or in state 1, and then the meter does not fill and the target is not seen.
- Seen (result 1) when the meter was above 1 before this check; else result 2 (noticing). No line of sight: result 0.
- Not in view: the meter empties by dt / 260 (`FUN_140853aa0`) and is dropped at 0.
- The target keeps a watcher list (+0x1f0, `FUN_140855f30`): level and time per looker; level 0 removes it.
  `FUN_1408586d0` shows, for entries younger than 4 s, an `arrow.mesh` entity per looker (`FUN_1405dc7d0`, pooled, render
  queue 0x57): dir = looker - sneaker (3D), tail at sneaker + (0, 12, 0) + dir x min(|dir| - 7, 15) (no skip when negative),
  turned from -X onto dir, unscaled. The mesh runs from its origin (shaft radius 0.5) to the tip at -X 10, head ±1.2
  (**Verified**, read with `OgreMeshReader`). Colour red 0xdd0000 detected, yellow 0xffdd00 noticing, green 0x00ff00 when a
  looker test (vtable +0x3f0, **Unknown**) passes. Material (`FUN_1409b7830`): `debug_marker` cloned as `debug_#rrggbb`,
  pass 0 `colour` = (rgb, 1); with its last argument set (this call) pass 1 is kept as (rgb, 0.2), else removed. The
  sneaker's state +0xe8 (`stealthUnseen`, **Observed** `FUN_1408586d0`) becomes 0 when it is fighting or a looker's meter is red, 2 when only yellow, else 1 (unseen).
- Name tag (`FUN_1406e8100`, colours loaded in `FUN_1406eb3a0`): shown within 10954 of the camera (1.2e8
  squared). Tag state 1/2/3 tints a stealth icon with the GUI colours `NameTagStealthHidden` / `AlmostSeen` / `Seen`,
  fallbacks (0.25, 0.521, 0.745), (0.745, 0.721, 0.25), (0.745, 0.25, 0.29) (**Verified** literals; the GUI XML may
  override). A second icon's frame is round((1 - x) x (frames - 1)), x = clamp(character +0xd8, 0, 1): the
  `LightLevel` image of `Kenshi_CharacterNameTags` (7 frames, frame 0 full light; **Verified**, kenshi_images.xml), on the
  panel's right in colour (0.942, 0.806, 0.341) (**Verified**, `Kenshi_CharacterNamePanel.layout`). The tag state is set alongside: 3 seen, 2 almost seen, 1 hidden.
- Stealth XP: see [character-stats](character-stats.md#levelling-xp).

Meitou: meters only for hostile/police lookers the aggro scan runs, no line-of-sight ray, f = 0.5, dt = the scan interval,
a meter decays also when the target is out of range (the original's rule there is **Unknown**). Crime witnesses keep the
same meters on a sneaking culprit in `CrimeSystem` (any faction, not just hostile lookers). A sneaking player
character gets the arrows (`MarkerRenderer`, both passes like the movement marker, lit by the sun; no green) and the original's stealth icon left of its name in the tag
colours: seen if any watcher detects, almost seen if any notices, else hidden, and the light-level icon from its `SightMap` light.
Every player character within the 1.2e8 range gets the `Kenshi_CharacterNamePanel` over its head (name in `Kenshi_StandardFont_Large` with shadow, selected: white in [brackets], else #b3b3b3 (**Observed**); the icon only while sneaking); NPCs get none
(**Observed** in the original, not verified against `FUN_1406e8100`).

## Hearing

Moving-state gate; ordinary noise lerp(speed fraction,350*f,600*f), stealth noise
lerp(stealth,450*f,80), f = lerp(s,1,0.25) dust / lerp(s,1,0.75) rain or acid / 1, from the mover's weather (no
protection term; **Verified**). `SensoryData::canHear` `FUN_140857500` (**Verified**), run only when sight failed, in order: both with the player: heard
within 1000, nothing else; a target neither moving nor in combat mode: not heard; another interior than the listener: only in combat mode and with one of them outdoors
(building to building never); another floor of the same building: only in combat mode, at most 1 apart, unless the building is cavernous (`+0xc2`); a per-pair
cooldown of 2 s of 1x game time divided by the speed (so 2/speed² real s; paused never) returns the last answer. Range N x0.5 when the listener's StateBroadcast
`+0xb4` (`isSleeping`, KenshiLib name **Observed**) is 0, x0.5 for another interior and x0.5 again when the building (the target's, else the listener's) has no open door.
Chance 0.75 x (1 - d²/N²), sneaking (0.05 when `+0xb4` is 0, else 0.1) x (1 - stealth) x the same; x2 when the entry is alarmed. In combat mode in the same place and on
the same floor any chance > 0 is a sure hearing; else a roll. A heard listener with `+0xb4` 1 drops it on a coin flip and, for a sneaking player target, says event 47
ALMOST_WOKE_UP. Sneaking or crawling targets get stealth XP ([character-stats](character-stats.md)). Meitou (`AggroSystem.HearPlace`): the interior, floor, door and
sure-hearing rules; not the cooldown, the `+0xb4` factors (which value an awake NPC holds is **Unknown**), the alarm doubling or event 47.
Hearing does not imply ordinary visual cone/line-of-sight eligibility.

## Retreat and memory

Sense entries keep position/time/seen/heard/faction/tags; tag map and update rates come from binary,
not renderer visibility. Runaway/forced-runaway, home-town retreat, retreat-refresh package and
squad Run-Away order have different target/ending rules. Faction ratios tune breaking/safe end;
normal squad trigger and exact selection remain partly unknown. Leash/home/running state must
not be equated with the attack planner or discarded immediately after a lost sighting.

### Sense tags: who is an enemy

**Observed** (decompile of the perception update `FUN_1408580d0`; bit names from the senses debug panel `FUN_1407981b0`). Each sensed
entry has a tag word (entry +0x30 → +0x30): bit 0 SENSE_ALLY, 1 SENSE_NEUTRAL, 2 SENSE_ENEMY, 3 SENSE_KO, 14 SENSE_CRAWLING; others
(5, 6, 7, 9, 10, 11, 12, 13, 15) are set from slave state, prisoner, own faction and carrying tests. The combat finders read it ([ai-tasks](ai-tasks.md#combat-goals)).
- Not the looker's enemy (Character slot 125, `vfunc_125` at 0x3e8): ALLY when slot 126 (0x3f0) says ally, else NEUTRAL.
- Enemy: NEUTRAL when `FUN_140796240` holds (the target's platoon is a campaign force, [ui-input](ui-input.md); Meitou `AggroSystem` Visit), else ENEMY (unless the looker's platoon object lacks a sensor that accepts it).
- **Slot 125** (`CharacterAnimal::vfunc_125`, shared by all characters): never the same faction. Across platoons the looker's squad's
  TEMP_ENEMY tag (or, for a looker whose faction has +0x250, the target's tag on it) makes an enemy and TEMP_ALLY prevents one. A disguise
  (uniform faction at +0x108 while +0x100 > 0.6 and +0x110 <= 0) stands in for the target's faction. A slave target: not when its owner
  is the looker's, and only when the looker is hostile to the owner's faction. A slave looker: never its owner's faction. A target of the
  faction the looker's platoon object +0x80 names (**Unknown** role) is an enemy. Otherwise hostile(looker faction → target faction),
  relation <= -30, when `FUN_1408569a0` fails and the looker's +0xea flag is clear (and the target's is clear, or the looker's faction is
  not real or default-hostile).
- So attack on sight is relations only: no fundamental-type BANDIT rule (**Observed**). In the base data the Shek Kingdom, Holy Nation and
  United Cities start at -100 only with Bloodraiders, Cannibals, Rebel Farmers, Grass Pirates, Gorillo Bandits and Yabuta Outlaws (Holy
  Nation also Holy Nation Outlaws); at -10 with Starving and Dust Bandits; at 0 with the other bandit factions (**Verified** by probe of
  the merged data). Fights lower relations ([factions 3.5](factions-squads-towns.md#35-what-fires-relation-changes-observed-decompile)), so a
  bandit faction a town keeps fighting can drop to hostile. The only BANDIT-type target rule found is the player's Shoot first gunners
  ([squad AI options](ai-tasks.md#squad-ai-options)).

### The attack-on-sight tests

**Observed** (decompile; goal 16's finder, [ai-tasks](ai-tasks.md#combat-goals)). A sensed character of a faction that
is not the looker's ally becomes a target only when both tests pass:
- **Area** (`FUN_140599f30`): true when the target stands in the looker's own building (character vfunc +0x98's
  +0x30 equals the target's vfunc +0x78). Else the AI's area test `FUN_140597790` by its manoeuvre mode (AI +0x2bc
  `ordersMovement`, KenshiLib `AIManuverabilityOrders`: 0 HOLD_GROUND, 1 STAY_CLOSE, 2 ROAM_FAR): ROAM_FAR around the
  looker itself (AI +0x2c0 set to its position), in when d^2 < 42467328 (about 6517 units); HOLD_GROUND around the
  centre of movement AI +0x2c0 (height from the state broadcast's position, AI +0x18 +0xcc), in when d < R + 40 + R'
  (R = AI +0x8 `meiDistance`, R' = AI +0xc `meiDistanceTolerance`) or d^2 < s^4 with s^2 the squared distance from
  the broadcast position to that point (as written); STAY_CLOSE in when d < R + 200 + R'. A target outside it still
  counts while it is fighting the looker (its combat object's +0x140 between 0 and 1 for the looker, `FUN_140665420`).
  The same position test bounds job targets ([range](ai-tasks.md#perma-jobs), ROAM_FAR by default). Writers:
  `AI::setManuveringFreedomLevel` from standing orders 8/9 ([combat](combat.md#fight-lifecycle-and-orders)); the centre by
  `AI::setCenterOfMovement`, which every player order calls with its point (`Character::addOrder`) and which in
  HOLD_GROUND / STAY_CLOSE also installs the hold-position task 30 / 31. The radii are the constructor's (`AI::AI`: meiDistance 18, tolerance 5; no other writer found), so HOLD_GROUND reaches 63 and STAY_CLOSE 223.
- **Post** (`AI::isTargetInRangeForHOLDMode_hostiles` `FUN_140598630`): only for a character on Hold (`CharStats::holdPositionMode`
  `FUN_1402b2b90`: CharStats +0x12b and not sneaking) that is not `FUN_1407f2840` (a ranged-weapon test): a target slower than 2 (movement
  +0xb8), or in its own state > 1, must be within 40 of the looker; a target more than 80 from the hold point
  (CharStats +0x130, set when Hold is turned on and to a ground move's destination while on Hold; 140 when the target is fighting and
  faces it) must be within 60 of the looker.
- So there is no give-up distance measured from where a fight began: an attack starts inside the AI's area, and a
  chase ends by time and lost senses ([Leaving a fight](combat.md#leaving-a-fight)).


### Summary

| Figure | Value | Label | Where |
|---|---|---|---|
| Vision range outdoors | 1500 x RACE `vision range mult` (0.5 to 2 in the data; 1 for 41 of 53 races) | **Verified** (literal) | `FUN_140851bc0` |
| Vision range inside a building | 500 (no race multiplier) | **Verified** (literal) | `FUN_140851bc0`, `FUN_140852f70` |
| Night | range x lerp(daylight, 0.5, 1): half at night | **Verified** (literals), daylight factor in [game-loop.md](game-loop.md#frame-clocks) | `FUN_140851bc0` |
| Weather (dust storm / other affecting weather) | range x lerp(s(1 - P), 1, 0.4 / 0.75), unclamped | **Verified** (decompile) | `FUN_140851bc0` |
| View cone | factor v = (cos a - 0.2) / 0.8; nothing beyond a = 78.5 degrees | **Verified** (literals) | `FUN_140852f70` |
| Seen when | v > 0.01 and d <= lerp(light, 0.2 or 0.4, 1) x contrast x R x v | **Observed** (control flow), constants **Verified** | `FUN_140852f70`, `FUN_140857b10` |
| Sneaking / crawling target | v x0.5 crawling; x0.75 or x0.5 sneaking; x lerp(stealth, 1, 0.6) | **Verified** (literals) | `FUN_140852f70`; Meitou `SightMap.Sees` (crawling or staying low) |
| Spot time of a sneaker | lerp(v, 0.5 + 5 s, 0.5) x lerp(f, 4, 1) s; meter > 1 = seen; forgets at dt / 260 | **Verified** (literals), flow **Observed** | `FUN_140856cb0`, `FUN_140853aa0` |
| Noise range of a mover | lerp(speed ratio, 350, 600); sneaking lerp(stealth, 450, 80); weather x0.25 / x0.75 | **Verified** (literals) | `FUN_140851d10` |
| Hearing chance per check | 0.75 x (1 - d^2/N^2); sneaking 0.1 (0.05) x (1 - stealth) x ...; x2 if recently alerted | **Verified** (disassembly and bytes) | `FUN_140857500` |
| Squad-shared sightings | a target a mate saw or heard counts as seen within 2 x R | **Observed** | `FUN_140857b10`, `GroupSense` |
| Gather per update | up to 40 objects within R; at 40, up to 20 more within 250 | **Verified** (literals) | `FUN_14085a8d0`, `FUN_1407866d0`; Meitou caps the look (`AggroSystem.GatherCap`) in grid order |
| Update rate | with the AI, in the logic tick: 8 characters per frame in turn | **Observed** | [game-loop.md](game-loop.md#the-character-update-budget) |
| Run away (Task_Runaway) | 2000 + 400 r away from the threats' centre, then hops of 1000 + 400 r; until no enemy sensed and nobody attacking | **Verified** (literals), flow **Observed** | `FUN_14034b440`, `FUN_140336820` |
| Squad not intact (retreat package) | 4+ members: fewer than 20% (40% with the leader down) able; under 4: leader down; no enemy counted; never while a member is imprisoned (`+0x2f8` 2) or carried by a non-ally (`FUN_14079ab40`). Meitou's `FleeSystem` trigger (the carried case is not modelled) | **Verified** (decompile `FUN_14079acf0`, `FUN_14079ab40`, `FUN_1407a5be0`) | `FUN_14079acf0` |
| FACTION `run away ratio of squad size` / `relative to enemy` | loaded into Faction +0xc / +0x10 (base data: 92 factions 0, others 0.2 to 0.5); not read: a scan of the game logic (0x140200000-0x140a00000) for float loads at those offsets found none | **Observed** (negative) | `FUN_1407fe270` |
| A lone character running away | `Task_Runaway`'s desire is 1 only when character `+0x5b8` is set, else 0.001: a fallback the planner takes when nothing else answers SELF_PRESERVATION | **Verified** (literals), flow **Observed** | `FUN_1405a34e0`, `FUN_140599d20` |
| Attack-on-sight area / post | ROAM_FAR: 6517 around the looker; HOLD_GROUND / STAY_CLOSE: R + 40 / R + 200 + R' around the centre of movement; Hold: 80 (140) / 60 / 40 | **Verified** (literals), flow **Observed** | `FUN_140597790`, `FUN_140598630` |

## Blood smell

**Verified** (decompile: `ZoneSmell` ctor `FUN_140a25b30`, `addSmell` `FUN_1405e83a0`, `periodicUpdate` `FUN_14090cef0`, `Smell::update` `FUN_1408f4740`). Every zone has two
channels: blood (rise 1/60, decay 1/240 per second) and eggs (rise 1/240, decay 1/60). Sources post each periodic update: 1 a corpse with flies, 2 a bleeding character (bleed rate
non-zero, body state 0), 3 eggs. Blood target = corpses + 0.25 per bleeder; position = the corpses' centroid, or the bleeders' when there are no corpses or at most 0.25 x the
bleeders (that branch partly lost in the decompile, **Observed**). The value moves toward the target at the rates, clamped. Corpse flies: [character-stats](character-stats.md#corpses).
176 HUNTING_BLOODSMELL (`AI::scoreHuntingBlood` `FUN_140794b60`, `Task_HuntingSmell`): animals only; the blackboard keeps a zone (`+0xf8`) while its blood is at least the animal's
ANIMAL_CHARACTER `smell blood` (`smell eggs` is the eggs threshold); else the best of its zone and its active island's, x0.8 for a neighbour, x0.5 otherwise, above the threshold;
desire 1 with one. The task runs (movement mode 2) to the smell's position; no end of its own. Data: "Smell Hunter" in the two Blood Hunter packages. SQUAD_TEMPLATE `blood smell mult`
is read into the platoon's patrol settings; its consumer was not traced (**Unknown**).

## Unknowns

- Slot 14 `+0xb4`, slot 60's `+0xd5`, the hearing re-roll factor, the meaning of
  character `+0x5b8` and `+0x504`.
- Who writes character `+0x5b8` (Task_Runaway at full desire) and AI `+0xbc` (`isNoEnemiesInVicinity`); the update cadence of the squad-intact test.
- The writer of a character's location handle `+0x80`; when can-see casts its physics ray; the building rules of can-hear;
  the alertness goal float and the +0x3f0 (green pointer) test of spotting.

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- 4. Can see: the order of tests (`FUN_140857b10`)
