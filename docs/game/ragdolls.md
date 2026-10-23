# Ragdolls

Runtime behaviour of character ragdolls in `kenshi_x64.exe`. The `.phs` file layout is in
[phs](../formats/phs.md); carrying/looting links in [carry-loot](carry-loot.md). Addresses are
functions of `kenshi_x64.exe`. Units are world units (0.1 m). Everything is **Observed** (static
reading of the cited function) unless marked otherwise. Our implementation and its engine choices:
[simulation](../simulation.md#ragdolls). **Verified** by `RagdollJointTests`: the human revolute
joints' bases and limits hold ≥99% of the male skeleton's clip poses (knees and elbows bend the way
the file allows).

## Middleware and scene

- **PhysX 2.8** (`NxCreatePhysicsSDK` version `0x2080400`, NxuStream loaders; the install ships
  PhysX system software 9.10.0513). **Verified**: import and constant in `FUN_1407dbee0`.
- Havok `hkb*`/`hka*`/`hkp*` ragdoll class names exist only as reflection data (`?@14210c…`, no code
  references); no ragdoll code uses them.
- Scene (`FUN_1407dbee0`): gravity (0, −98.1, 0); max timestep 1/60, max iterations 16, time-step
  method 1 (variable); flags 0x44; hardware simulation tried first, software on failure.
  SDK parameters: skin width 0.25, default sleep linear velocity² 2.25, angular velocity² 1.96,
  dynamic and static friction scaling 4.0. Four materials share dynamic friction 1.0, static 20.0,
  restitution 0.05 (the default material too); ragdoll shapes use the one at physics `+0x360`.
- PhysX call meanings (flags, damping, sleep, forces) come from NxActor/NxScene vtable slots matched
  to the published 2.8 SDK headers.
- Stepping (`PhysicsActual::vfunc_2` `FUN_1407dd060`, background thread): `simulate(min(dt, 0.04))`
  (0 while paused), flush, blocking fetch.

## Collision groups

A ragdoll's shapes get group **3**, or **4** while character slot 103 (`0x338`, `FUN_1405c8740`:
true roughly when inside a building) is true; `FUN_1407d3c00` swaps it as that changes (not in carry
mode). Groups 3/4 are also items ([collision](../formats/collision.md#collision-groups-observed-fun_14055f6e0-and-the-loaders)).
Pairs disabled in `FUN_1407dbee0` (third argument hidden on a few calls, assumed 0 like the rest):

| Group | Does not collide with |
|---|---|
| 0, 1 | 6, 9–12, 14, 15–18, 23–26 |
| 2 | 1, 4, 6, 14, 28–31 |
| 3 | 0, 1, 5, 6, 7, 23–26, 28–31 |
| 4 | as 3, plus 2 and 14 |
| 5 (doors) | 0, 1, 3, 4, 6, 14, 23–26 |
| 7 | every group 0–31 |
| 28–31 | 0, 1, 3, 4, 14, 23–26, 6 (last pair's group assumed) |

So ragdolls hit each other, items, building floors/stairs/furniture/roofs (9–22, 27), but not doors,
foliage group 6 or passable parts.

## RagdollPart and requests

`enum RagdollPart { NONE=0, WHOLE=1, RIGHT_ARM=2, LEFT_ARM=4, HEAD=8, RIGHT_LEG=16, LEFT_LEG=32,
CARRY_MODE=2048 }` (**Verified**: `fcs_enums.def`; LOCATIONAL_DAMAGE `collapse part` uses it). The exe
also uses 0x8000 internally as "release all".

- The active mask is AnimationClass `+0x2f8` (`FUN_1407d1390` all-bits test, `FUN_1407d13e0` WHOLE,
  `FUN_1407d13c0` CARRY_MODE). "Ragdolled or carried" `FUN_1407d1440` = Character `+0x3d4` or the
  RagdollAnimation WHOLE/CARRY flags.
- `FUN_1405cbd60(character, on, parts)` queues `{on, parts}` in Character `+0x3e0`; a queued
  opposite request for the same parts cancels both, a duplicate is dropped. Only when the character
  has a visual or is dead (slot 31) and, for "on", is not in furniture (Character `+0x2f8`, the
  bed/cage state of [carry-loot](carry-loot.md)).
- The queue is drained at the sync point `FUN_1407d2380` → `FUN_1405d2c30` → setter `FUN_1405d0db0`,
  which keeps a request queued while the skeleton is not ready or a get-up blend runs.
- The setter, for WHOLE or CARRY_MODE: unlinks a carried body (also when the carrying arm goes limp),
  stops movement, puts the weapon away (slot 90), loads RACE `male ragdoll` / `female ragdoll`, then
  AnimationClass slot 10 (`FUN_1405b9a10`; Human `FUN_1405ba050` also records the arm bits).
  Notify slot 132: humans holding a weapon in a limp arm put it away (`FUN_1405cc230`).
- RACE `male carry ragdoll` / `female carry ragdoll` are **Verified** unused: no string reference in
  the exe. RACE `carry bone` is read by `FUN_1407d3850` (falls back to the skeleton root).
- Animals ragdoll through the same path and fields (`female ragdoll` only for the appearance's
  female flag), but get no partial limb ragdolls.
- Off requests clear bits; at mask 0 the physics ragdoll is destroyed (`FUN_1407d3000`) and the
  animation tracks are re-enabled.

## Triggers

All callers of `FUN_1405cbd60` / `FUN_1405ce9c0` / `FUN_1405d2d20` (part arguments **Verified** in
the disassembly where the decompile hides them):

| Trigger | Request | Where |
|---|---|---|
| Death | on WHOLE unless carried | `FUN_1407a6200`; dead-body tick `CharacterHuman::vfunc_29` re-requests while not ragdolled |
| Death while down | on WHOLE, or CARRY_MODE if carried | `CharacterAnimal::vfunc_64` |
| Knockout, bleed-out (blood ≤ 0 / −25) | on WHOLE | damage `FUN_14064f300` |
| Medical tick while unconscious (`+0x161`) | on WHOLE, or CARRY_MODE if carried | `FUN_140649320` |
| Limb below its collapse level | on with the part's `collapse part` bits, humans only, not for a robotic limb on that side | `FUN_140649870` |
| Limb recovered | off for arm/leg bits no longer collapsed | `FUN_140649320` |
| Picked up | on CARRY_MODE (replaces a queued WHOLE) | `FUN_1405ce9c0(1)` |
| Put down / dropped | off CARRY_MODE; the medical tick then re-requests WHOLE | `FUN_1405ce9c0(0)` |
| Get up | off WHOLE unless carried | `Task_GetUp::vfunc_1` → `FUN_1405c69f0` |
| Furniture use, entity rebuild, destruction | release all | `FUN_140330600`, `FUN_1405b93a0`, `FUN_140799af0` |

No path from hits, knockback, stagger or explosions: a conscious character is never thrown
([combat](combat.md): knockback is root motion). Animals play `VO_Creature_Die` on a WHOLE request.

### Forced crawl

**Observed** (decompile). Posture (slot 109/110, field `+0xe0`, setter `FUN_1405c7e20`): 0 standing,
1 voluntary crawl (orders reset it unless `+0x5b8` forces it), 2 forced crawl, 3/4 set while ragdolled.
The character tick `FUN_1405d00b0` resets 2 to 0; the medical tick `FUN_140649320` (while conscious) sets
it back to 2 when **any one** part with `collapse part` 0x10/0x20 tests down, and keeps those leg ragdoll
bits on. So it lasts exactly while a leg is down; it does not knock out.
- Leg test `FUN_140644450`: down when a stump or crushed (limb state 1/3), when h − s < −(base × mult ×
  scale) − wear (severed), or when h − s + b (bandage, `+0x48`; not the rig `+0x4c`) is under the KO point
  `FUN_140643ae0` (−10..−85 by toughness), not under 0. Animals (slot `0x248`, `asAnimal`) never collapse.
  Between 0 and the KO point a leg only limps (leg factor, [speed](character-stats.md)).
- Posture ≠ 0 picks category 5 GROUNDED lower clips (`AnimHuman_LowerCombat` `FUN_14051fc60`); base data
  (rebirth.mod): `crawling move`, `crawl idle up` (any combat mode), `crawl idle down` (out of combat), `crawl use`.
- Defence techniques must have `is prone` equal to posture ≠ 0 (`FUN_140887970`): a crawler has no block or
  dodge (no prone one exists). The attack pick's test was not traced; the only prone techniques are
  `ground attack L/R`, and Meitou's chooser applies the same rule to attacks (engine).
- Meitou: `HealthPart.LegDown`, `MedicalState.Crawling` (KO point cached each medical tick), `AnimationSystem.Crawl`
  (engine choices: `crawl use` replaces the medic clip; crawl legs weighed at no less than −100). The ×0.5
  sight factor for a crawler ([perception](perception.md)) is not applied to crawlers who are not sneaking.

## Bodies: kinematic or simulated

The ragdoll (`FUN_1407e4ed0`, `ScytheRagdollPhysicsT`) is created at the scene node position
(character position if zero/invalid; for WHOLE outdoors snapped to terrain height when below it or
over 500 above), scaled by character slot 114 (1.0 for humans, age-lerped size for animals).
Each new body (`ScytheRagdollPhysicsT::vfunc_8` `FUN_1407e2ae0`): kinematic, FILTER_SLEEP_VEL,
linear and angular damping 0.5, collision on. `AnimationClass::vfunc_11` (`FUN_1405b8720`) then
marks each bone **pinned** (kinematic, follows animation) or **simulated** and calls
`FUN_1407d1d20(ragdoll, 1, pinned, bone)` (**Verified** argument order in the disassembly):

| Mask | Simulated bones | Pinned |
|---|---|---|
| WHOLE | all | none |
| LEFT_ARM / RIGHT_ARM | that UpperArm, Forearm | Clavicle and the rest |
| LEFT_LEG / RIGHT_LEG | that Thigh, Calf | Foot and the rest |
| HEAD | Head, Neck | the rest |
| CARRY_MODE, human | both Forearms, Head | the rest (animated carried pose) |
| CARRY_MODE, animal | all but the carry bone | carry bone |

Activating a simulated body: clear kinematic, ENERGY_SLEEP_TEST, linear damping 0.05, angular 0.01,
sleep energy threshold 5.0, wake 0.4 if asleep. No joint motors or springs are set: a "powered"
pose exists only as pinned kinematic bones.

## Initial state

`FUN_1407d1800` places every body at its bone's current animated world pose and gives simulated
bodies zero velocity. The setter passes the character's movement velocity (Y zeroed, dropped if
|v|² > 100), but slot 10 never uses it (**Verified**: its register is reused after the clamp). No
hit impulse exists. `FUN_1407d2d10` (per-limb speed caps) and `FUN_1407d2290` have no callers.

## Per frame

Driver `FUN_1405b8180` (AnimationClass), update `FUN_1407d3c00`:

- Root: for WHOLE (or a carried animal) the scene node is set to the root body's position
  (`FUN_1407d1490`: the body named at RagdollAnimation `+0x48`, else the first simulated one);
  otherwise the node keeps the movement position.
- Bones (`FUN_1407d3a30`): a simulated body writes its orientation into its manually controlled Ogre
  bone (plus position for a bone without parent); a pinned body is moved to the animated pose.
- Carried animal: the carry-bone body is moved kinematically to the carrier's attach bone.
- Partial (non-WHOLE) ragdolls are woken every frame (wake 20) and never freeze.
- Water (only when byte `+0x79` of the character's race data is set): when the root is at or below
  y 100 (water level), each body below it gets an upward
  acceleration 98.1 + ln(100 − y + 1), and the freeze timer runs at half speed.
- Character simulation position `+0x48` = root body position while ragdolled or carried
  (`CharacterAnimal::vfunc_28` via `FUN_1407d16f0`): looting, carrying and AI distances use it.

## Sleep and limits

Freeze = every body put to sleep and updates skipped (`FUN_1407d1f70`), WHOLE only:
- after 2 s of ragdoll, within the next 5 s when ≥ 60% of bodies sleep; after 5 s a downward ray
  from 20 above the node against groups 2, 9–12, 15–22 (mask 0x7f9e04) decides (`FUN_1407d2440`);
- at once when the terrain tile under it is not loaded (`FUN_1405b8180`).

The frozen flag clears when a body wakes (`FUN_1407d2050`). `FUN_1407d4270` (reset timers, wake all
with 0.4) runs when bone flags are applied and when a frozen ragdoll is carried. A root
more than 200 below the terrain is lifted to it and frozen (`FUN_1407d4470`). Global flag
`DAT_1421349c9` (meaning Unknown) disables freezing and buoyancy. No distance LOD or ragdoll count cap was found
(`DAT_142133080` is a live-actor counter only).

## Getting up

`Task_GetUp` (`FUN_14033ca10` start, `FUN_140334a20` update):
- Clip: humans `standing up 3`; `crawl idle down` with leg state 1–2; `swim idle` in deep water
  (`FUN_1405c7fd0` level 3); animals their state-matching idle (`AnimationClass::vfunc_25`). No
  face-up/face-down variant.
- `FUN_1407d36e0` → `FUN_1407d3130`: heading = root→`Bip01 Head` direction flattened to the ground
  (rotation from +Z); each bone blends from its ragdoll pose to the clip's first key over 0.75 s
  (`FUN_1407d28e0`: nlerp rotation, lerp translation), then the bones return to animation.
- The task ends when the clip passes 0.86, or at once when crawling.

## Saves

No ragdoll key or pose is saved. After loading, the medical tick and the dead-body tick re-request
WHOLE from the loaded animated pose.

Meitou adds GAMESTATE_CHARACTER string `meitou ragdoll` for a whole-body (not carried) ragdoll: `frozen age n`, then per body (definition order)
position x y z and rotation x y z w, invariant floats; absent otherwise (a world without ragdolls keeps the loaded key); the original ignores it. **Observed** (headless, real data, a knocked-out
human frozen after 61 ticks): save → load gives the same 13 body poses (offset 0), frozen flag and age; without the key it falls again from the clip.

## Unknowns

- Meaning of groups 0, 1, 2, 7, 28–31 and the terrain's group.
- Exact condition of character slot 103 (group 4) and the downward-ray branch of `FUN_1407d2440`.
- Which body is the root (RagdollAnimation `+0x48` key); likely the pelvis.
- `FUN_1405ce9c0`'s branch for Character `+0x388` 1 / 0x5b (calls `FUN_1405b4fa0` on put-down).
- Purpose of the linked Havok libraries; meaning of race-data byte `+0x79` (buoyancy gate).
- Which LOCATIONAL_DAMAGE parts set HEAD in `collapse part` in the shipped data.
