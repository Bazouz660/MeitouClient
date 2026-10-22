# Perception and fleeing

Original sense/GroupSense/vision/hearing/runaway research. Binary literals are Verified;
interpreted flags/control flow stay Observed or Unknown. AI target choice: [ai](ai.md);
relations: [factions](factions-squads-towns.md). Simulation senses/leash remain approximations.

## Sight

Range500 indoors or1500*race-mult outdoors, daylight lerp0.5..1. Weather reduces by strength/
protection: dust0.4, other affected weather0.75; inside/upper-floor/slot60 branches differ.
Cone=(cos(angle)-0.2)/0.8, zero past78.46deg. Crawling/ground posture*0.5; sneaking/posture
adds0.75/0.5 and lerp(stealth,1,0.6). Stealth scales skill*carry0.75*crawl2*load/equipment/race/
injury, clamps0..1. Light contrast clamp(target-light-looker-light+1,0,2), ambient clamp(mean,0,1),
light floor0.2 (no home town0.4). Visibility=light*contrast*range*cone; require>0.01.

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
  `FUN_1408586d0` draws, for entries younger than 4 s, a short pointer at the sneaker (12 up, length
  min(d - 7, 15) towards the looker): red 0xdd0000 detected, yellow 0xffdd00 noticing, green 0x00ff00 when a
  looker test (vtable +0x3f0, **Unknown**) passes. The sneaker's state +0xe8 becomes 1 (any red) or 2 (any yellow).
- Name tag (`FUN_1406e8100`, colours loaded in `FUN_1406eb3a0`): shown within 10954 of the camera (1.2e8
  squared). Tag state 1/2/3 tints a stealth icon with the GUI colours `NameTagStealthHidden` / `AlmostSeen` / `Seen`,
  fallbacks (0.25, 0.521, 0.745), (0.745, 0.721, 0.25), (0.745, 0.25, 0.29) (**Verified** literals; the GUI XML may
  override). A second icon's frame is round((1 - x) x (frames - 1)), x = clamp(character +0xd8, 0, 1), probably the light
  level of the sun icon (tutorial text; **Observed**). How tag state 1-3 is derived from +0xe8 is **Unknown**.
- Stealth XP: see [character-stats](character-stats.md#levelling-xp).

Meitou: meters only for hostile/police lookers the aggro scan runs, no line-of-sight ray, f = 0.5, dt = the scan interval,
a meter decays also when the target is out of range (the original's rule there is **Unknown**). Crime witnesses keep the
same meters on a sneaking culprit in `CrimeSystem` (any faction, not just hostile lookers). A sneaking player
character gets the pointers (from 0.35 of their length to the tip, with a head) and the original's stealth icon left of its name in the tag
colours: seen if any watcher detects, almost seen if any notices, else hidden. No light model, so no sun icon.
Every player character within the 1.2e8 range gets the `Kenshi_CharacterNamePanel` over its head (name in `Kenshi_StandardFont_Large` with shadow, selected: white in [brackets], else #b3b3b3 (**Observed**); the icon only while sneaking); NPCs get none
(**Observed** in the original, not verified against `FUN_1406e8100`).

## Hearing

Moving-state gate; ordinary noise lerp(speed fraction,350*f,600*f), stealth noise
lerp(stealth,450*f,80), weather f dust0.25/rain-acid0.75 scaled by strength. Building/floor/squad
branches halve or reject range. Probability ordinary0.75*(1-d²/N²), sneaking0.1 or0.05 times
(1-stealth)*(1-d²/N²), alert doubles. Re-roll time and1000-unit special predicate unknown.
Hearing does not imply ordinary visual cone/line-of-sight eligibility.

## Retreat and memory

Sense entries keep position/time/seen/heard/faction/tags; tag map and update rates come from binary,
not renderer visibility. Runaway/forced-runaway, home-town retreat, retreat-refresh package and
squad Run-Away order have different target/ending rules. Faction ratios tune breaking/safe end;
normal squad trigger and exact selection remain partly unknown. Leash/home/running state must
not be equated with the attack planner or discarded immediately after a lost sighting.

### Summary

| Figure | Value | Label | Where |
|---|---|---|---|
| Vision range outdoors | 1500 x RACE `vision range mult` (0.5 to 2 in the data; 1 for 41 of 53 races) | **Verified** (literal) | `FUN_140851bc0` |
| Vision range inside a building | 500 (no race multiplier) | **Verified** (literal) | `FUN_140851bc0`, `FUN_140852f70` |
| Night | range x lerp(daylight, 0.5, 1): half at night | **Verified** (literals), daylight factor in [game-loop.md](game-loop.md#frame-clocks) | `FUN_140851bc0` |
| Weather (dust storm / other affecting weather) | range down to x0.4 / x0.75 at full strength, less with protection | **Observed** | `FUN_140851bc0` |
| View cone | factor v = (cos a - 0.2) / 0.8; nothing beyond a = 78.5 degrees | **Verified** (literals) | `FUN_140852f70` |
| Seen when | v > 0.01 and d <= lerp(light, 0.2 or 0.4, 1) x contrast x R x v | **Observed** (control flow), constants **Verified** | `FUN_140852f70`, `FUN_140857b10` |
| Sneaking / crawling target | v x0.5 crawling; x0.75 or x0.5 sneaking; x lerp(stealth, 1, 0.6) | **Verified** (literals) | `FUN_140852f70` |
| Spot time of a sneaker | lerp(v, 0.5 + 5 s, 0.5) x lerp(f, 4, 1) s; meter > 1 = seen; forgets at dt / 260 | **Verified** (literals), flow **Observed** | `FUN_140856cb0`, `FUN_140853aa0` |
| Noise range of a mover | lerp(speed ratio, 350, 600); sneaking lerp(stealth, 450, 80); weather x0.25 / x0.75 | **Verified** (literals) | `FUN_140851d10` |
| Hearing chance per check | 0.75 x (1 - d^2/N^2); sneaking 0.1 (0.05) x (1 - stealth) x ...; x2 if recently alerted | **Verified** (disassembly and bytes) | `FUN_140857500` |
| Squad-shared sightings | a target a mate saw or heard counts as seen within 2 x R | **Observed** | `FUN_140857b10`, `GroupSense` |
| Gather per update | up to 40 objects within R; at 40, up to 20 more within 250 | **Verified** (literals) | `FUN_14085a8d0`, `FUN_1407866d0` |
| Update rate | with the AI, in the logic tick: 8 characters per frame in turn | **Observed** | [game-loop.md](game-loop.md#the-character-update-budget) |
| Run away (Task_Runaway) | 2000 + 400 r away from the threats' centre, then hops of 1000 + 400 r; until no enemy sensed and nobody attacking | **Verified** (literals), flow **Observed** | `FUN_14034b440`, `FUN_140336820` |
| Squad not intact (retreat package) | 4+ members: fewer than 20% (40% with the leader down) able; under 4: leader down | **Observed** | `FUN_14079acf0` |
| FACTION `run away ratio of squad size` / `relative to enemy` | loaded, no reader found | **Unknown** | `FUN_1407fe270` |
| Leash / give-up-chase distance | not found | **Unknown** | - |

## Unknowns

- The enemy tests of the attack-on-sight finder and its range (goals: [ai-tasks](ai-tasks.md#combat-goals)).
- Slot 14 `+0xb4`, `+0x2f8` = 1, `FUN_140436610`, `FUN_140856bc0`, slot 60's `+0xd5`, the hearing re-roll factor, the meaning of
  character `+0x5b8` and `+0x504`.
- Whether the run-away ratios are read at all; the leash.
- The building rules of can-see and can-hear; the alertness goal float and the +0x3f0 (green pointer) test of spotting.

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- 4. Can see: the order of tests (`FUN_140857b10`)
