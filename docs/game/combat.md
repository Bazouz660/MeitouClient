# Combat

Original research: CharStats/CombatClass/MedicalSystem/ranged controller, FCS records and binary
constants. Tables/formulas/evidence scopes retained below; [stats](character-stats.md) owns medical
state/XP, [animation](../animation.md) owns layers/root motion. Implementation: Gameplay.Combat,
Simulation.Combat and Animation; approximate engine scheduling is not original parity.

## Weapons and damage

**Observed** condition-adjusted weapon skill/strength/dexterity, stat scale m:
cut=lerp((skill+dex*m)*cutMult/200,cutBase,cut99);
blunt=lerp((0.5*skill+1.5*str*m)*bluntMult/200,bluntBase,blunt99), interpolation unclamped;
pierce=0.78*(skill+dex*m)*pierceMult+10*pierceMult. Stored cut/blunt endpoints13/52;
blunt-dominant cutBase=0.4*blunt1; other bluntBase not decoded. Packet cut/blunt/pierce/stun0,
bleed and penetration then receives target-kind/race/general multipliers. Manufacturer/quality,
weight/strength and skill speed chains are separate from this packet.

Unarmed category5: martial fraction lerp(skill/100,0.25,1); damage combines toughness/strength/+10,
min1; blunt fraction lerp(skill/100,1,0.4), remainder cut, no pierce. Exact term shapes uncertain.
**Verified** unarmed CONST0.8 binding. Stumble threshold toughness*m*0.52 plus state eligibility.

## Techniques, block and dodge

Technique direction/category/weapon/state/reach/animation flags gate selection; attack versus
defence/dodge skill includes injuries/equipment. **Observed** outcome: block5 if matching block
past50% animation; dodge0 within progress0.1..0.98; otherwise hit2. Skill roll r1*A<r2*B,
A=1+0.05*max(0,s2-s1), B=1+0.05*max(0,s1-s2). Derived chance with k=B/A is1-1/(2k) ifk>=1,
elsek/2. General skill blend0.01*(0.6*stat+0.4*dexterity*m) feeds dodge/reload, with stat/gear
and injury variants. Technique power/max-encumbrance and some length/selection details unknown.

## Hit reactions

**Verified** (`FUN_1404391f0` from `FUN_1404394f0` on a hit): damage = cut+blunt+pierce+stun against the
stumble threshold. A stumble clip is picked unless the hit is light and one is already playing; it is big
only when heavy and none was playing; front/rear by a dot test (`FUN_140435600`). Humanoids only. "Heavy_Hit"
/ "Light_Hit" are only the Wwise "Impacts" switch (Heavy = a big clip). A picked clip cancels the defender's
own technique (its blow never lands) and holds it in the stumble state until the clip ends (`FUN_140665490`,
`FUN_14060cf60`, `FUN_140435570`); with no clip the technique is still cancelled. Every 1.2 s of the stumble
(`+0x124`) it may break into a block against a swing at 10–99% progress (block-chance roll `FUN_140885d80`).
Knockback is the clips' root motion only (**Observed**: no impulse in these functions; [animation](../animation.md); ragdolls: [ragdolls](ragdolls.md)).
A blocked blow neither staggers nor pushes (`FUN_140665540`). **Observed**: a shackled defender skips 25%.

## Pacing

**Verified**: after an attack (clip ≥98% done) the fighter waits 0.6 s after a hit, 0.3 s blocked, 0.2 s dodged
(`FUN_14060a2f0`, `FUN_14060d6a0`; flat, no stats). Before swinging, an attacker in reach asks its target
(`FUN_14060c670` → `FUN_140666a30`): target swinging at it → the attacker blocks; target stumbling or blocking →
attack; otherwise an initiative roll on both attack skills (`FUN_140665370`, the skill roll above): the winner swings
and the loser blocks (or circles 1.5–4.5 s at ±25 when the target does not face it, **Observed**). No free attack
slot (`max num attack slots` + race extra, `FUN_140666310`) → circling. `max simultaneous hits` caps the targets of
one swing; `power N` multiplies hit N's packet; `limb N` picks the punch/kick sound.

**Observed** (decompiled, not measured in game), with several attackers: a target answering a request is asked by
anyone, not only its own target, and the winner of the roll turns to attack the asker (`FUN_140666a30`). Deciding
(`FUN_14060c8b0`) tries attacking first and defends only when that fails; a waiting fighter that is attacked goes
to deciding. During its own swing a fighter aborts to defend only while under 50% of it with an incoming blow past
50% (`FUN_14060a970` → `FUN_1406083d0(0.5, 0.5)`); while anyone attacks it the swing ends at the current blow's stop
frame (`FUN_14060a590`). The defence covers the most advanced incoming blow from anyone; a failed defence roll means
no defence for 1.2 s (+0x14c). A dodge plays its whole clip at its own speed, then decides at once; a block that
ends leaves 0.2 s.


## Combat stance

**Observed** (static reading; the animation's combat flag is `AnimationClass +0x244`, the value a clip's `is combat mode` is compared with, [animation](../animation.md#choosing-candidates--14051d660)). One setter, `FUN_14051cc30`, writes it. Set on: every tick of the fight's attack update (`CombatClass::vfunc_11`), and by the combat footwork (`FUN_1402ae1b0`, from a movement flag, not from the target). Cleared by: any new movement goal (`CharMovement::vfunc_18`), the end of the fight task (`CombatClass::vfunc_2`, mode 2), and the chase branch of `CombatClassAI::vfunc_9` (fight states 10 and 11, which also put the movement back to its plain mode 0; every other state sets movement mode 1). When a movement stops (`CharMovement::vfunc_19`) the flag is set to whether the fight task is active (`FUN_140436610`, `CombatClass +0x130`, set at task start). So the stance follows the fight's own steering and is off while the character walks a path to a goal, including a chase of its target along a path (states 10/11). `CombatClassAI::vfunc_9` sets `+0x58` each tick when the navmesh line query between the two (`FUN_1403a56e0`, 1 clear, 0 blocked, −1 failed) returns 0; that, or the two being in different places (a vtable `0x60` id of each differs), or the 2 s chase timer `+0x14c` still running, picks the chase; otherwise the AI picks its target and approach (`FUN_14060c8b0`, state 3) and steers with mode 1.

**Observed** (static reading, the fight steering `FUN_1402af1e0`; its constants are literals in the decompile): the footwork flag `FUN_1402ae1b0` reads is a "running" flag. With gap = distance − (own radius + target's radius), it is set when gap > 90 (> 160 in a steering mode 6, not decoded) and cleared when gap < 70; then forced on while the target is fleeing (`FUN_1402ae0e0`: target's movement in plain mode 0, speed > 9, for 1.5 s on end; the chaser then aims at a lead point, below). The anim combat flag is `!running`. Speed: up to `min(cap, own max speed)` (the AI's cap is 200, so the max speed), ramping 15 units/s per second; not running it is capped at 18 (16 backing off). So a fighter runs normally from afar and drops into the shuffle within 70 units of its target, distance-driven, whether or not it walks a path.

**Observed** (static reading, `FUN_1402af1e0` → `FUN_1404354d0`; the stat from `FUN_1408866b0` → `FUN_140883a80`): the lead point is the target's position + its velocity (Character vfuncs 9 speed × 10 heading, movement +0xb8 and +0xd0) × `(1 − r) × 2 + 1.2` seconds, recomputed every tick, with r the chaser's CharStats +0x188: its attack play rate (lerp(0.01 (0.6 dex + 0.4 attack), 0.8, 1.2) × G, capped at 1.2, the rate attack clips play at; × 0.8 when `FUN_1405c7fd0(character) == 3`, condition not decoded). So 0.8 s ahead at r = 1.2, 1.6 s at 0.8, more for a slowed attacker; a faster chaser aims past the runner, and since the lead follows the velocity it swings to the new side when the runner turns, and drops as soon as the runner slows under 9 or leaves plain movement (the 1.5 s timer restarts). The steering faces its movement while leading. A second override (`CharMovement::vfunc_28` sets movement +0x168 and a point at +0x16c, the steering's +0x50/+0x54): the chaser heads for that point instead at most 55 u/s, the lead is off, and it clears within 10 units; `CombatClassAI::vfunc_9` skips its chase while it is set. Its caller is **Unknown**.

Meitou (`PursuitSystem`, engine rules): the lead is a path goal, re-pathed when it moves over 12 units (at most every 0.5 s, so a turn can overshoot by ~12 units at a run), and used only while the chaser is behind the target (target velocity · (target − chaser) > 0); level or ahead it goes for the target and stops within reach, so the runner comes into it. A lead off the navmesh falls back to the target. The +0x168 override is not modelled.

Data (**Verified**, the base ANIMATION records through `AnimationLibrary`): with the flag on, the lower clips are the `walk lower combat shuffle` trio (speeds −99 to 50) and `jog lower combat fallback` (50 and up); `walk`/`jog`/`run lower` are combat NO.

**Unknown**: the extra offset the steering adds while leading in a non-zero steering mode (`FUN_1402e9420` at radius + 40, `FUN_1402e9530`) and the side terms of `FUN_1402e93c0`; whether the original also holds a chaser from swinging while behind; the steering's radii (`+0xe0` and `FUN_14065dc30`) and mode 6 (possibly a ranged closer; Meitou uses 90 for every fighter); what makes the AI conditions `iAmBeingMeleeAttacked immediate` / `general` (ids 158 and 159, registered in `FUN_140610250`) true (they read a flag byte at `+0x2b0` of the object at Character `+0x2f8` and an int at `+0x58` of the object that object's vtable slot `0x68` returns; no writer found), and so when a player's character that is idle starts its fight task when attacked. Meitou: `RetaliationSystem` engages it at once, and `PursuitSystem` keeps the running flag by the rule above ([simulation](../simulation.md#animation)).

## Armour and medical hit

Coverage rolls, slot order, armour class/material quality and cut/blunt resist/efficiency affect
packet before a weighted body-part hit. Armour absorption/penetration and cut-to-blunt conversion
are distinct; never collapse protection into one damage multiplier. Apply blunt/cut, wound,
stun, bleed and reaction through MedicalSystem, then XP. Health, bandage, stun, blood and
KO/coma/death thresholds use [character-stats](character-stats.md). Never tick medicine twice.

## Ranged and turrets

Controller phases reload → steady aim → fire; target motion/range, skill/injury, equipment and
line of sight determine timing/spread. Bolts retain launch/direction/velocity/impact/friendly-hit
state and consume ammo charges; do not model hitscan. Turret objects and mounted ammo/reload
branches differ from carried crossbows. Player/NPC targeting and friendly-fire training have
separate eligibility. The retained field/constant tables and expression list specify decoded
values; trajectory, aim timing and obstruction uncertainties remain labelled.

Turret aim (**Verified**, `TurretBuilding::vfunc_28` `140434b70`, `RotatingEnt::vfunc_2`): the turning parts are the BUILDING_PART records with `rotation function` 5; each turns toward the aim point by its angular error
(yaw about Y, or pitch) limited to ±GUN_DATA `aim speed` × efficiency × frame time, one after the other down the part chain. Movement and its sound counter: [audio.md](../audio.md#other-world-side-events).
**Unknown:** which target a manned turret picks (`FUN_140437f40`), its idle behaviour, and the wall bonus to the turning speed. Meitou turns turrets only for sound (display only, nearest hostile in `range`; no firing or damage model).

## Stealth knock-out

Order 228 "Stealth KO" (verb list `FUN_14079b2d0`), `Task_StealthKO` (vtable slots `FUN_140345eb0` start,
`FUN_14034cd80` update). **Verified** literals, flow **Observed**:

- The attacker walks to the target's position minus a vector of the target (+0xd0 of its body object, **Unknown**);
  within 14 of it the swing starts and it turns to face the target. The task ends at once when the target senses the
  attacker (`FUN_140855830`).
- Swing: clip `knockout` at x1.2 (record `knockout`, anim `stealthKO` in rebirth.mod); at 0.5 of it the roll, at 0.85
  the end.
- Chance (`FUN_1408852f0`): X = attacker assassination x injury mult minus the target's max of assassination, 0.5 x
  thievery, 0.5 x stealth term x 100, 0.5 x melee defence, toughness (each x its injury mult); chance =
  100 x (X + K) / 2K, K = 30 for X >= 0 else 15 (disassembly: the hidden third argument is +K). Unclamped. 0 when the
  target's sense entry of the attacker has its +0x38 flag (it sees the attacker); x0.5 with +0x39 (**Unknown**).
- Roll uniform 0..100; lands when chance > roll. Hit: floating "*Thunk*", target KO time `FUN_140644980` with
  assassination x 0.01 (the put-down KO formula, [carry-loot](carry-loot.md)). Miss: "Failed!", the target turns on
  the attacker. Assassination XP via the generic entry, dt 1: 100 x f on a hit, 300 x f on a miss; f = 0.5 for a
  sleeping target (+0x2f8 == 1), 0.07 for +0x2f8 == 2, else 1; " (No XP)" under an **Unknown** test.

Meitou: the point is 8 behind the target's facing; "senses" = the target's spot meter on the attacker is full or it
is fighting; the +0x39 halving, the 0.07 case and No XP are not modelled; a swing is reported as an Assault (the
original's crime call is not traced). The verb shows the chance; a quick right click on an NPC while sneaking gives it.
Order 229 "Stealth Kill" shares the task; not modelled.

## Validation

Game record/enumeration/constant checks are scoped separately from hand-decoded runtime formulas.
Synthetic determinism tests verify our implementation/order, not original-game equivalence.
Use researched endpoint/coverage/KO/ranged cases and preserve unknown terms rather than assigning
unverified constants. [Simulation](../simulation.md#combat) describes current wiring.

### Class map

| Class (RTTI) | Role |
|---|---|
| `Gear` | item base; `+0x220` quality fraction (level·0.01), `+0x224` level int. **Verified** (ctor `1400d3090`, and `1400ce2d0` lerping by `+0x220`) |
| `Weapon`, `Sword`, `Crossbow` | weapon items; `Sword` is the melee class, `Crossbow` creates the gun data |
| `Armour` | worn armour; loader `140898e90` |
| `CharStats` / `CharStatsAnimal` | stats, skills, per-hit damage maths |
| `MedicalSystem` | body parts, blood, KO, death; per-part `HealthPartStatus` |
| `CombatClass`, `CombatClassAI`, `CombatState`, `AttackState` | per-character combat state |
| `GunClassPersonal`, `GunClassTurret`, `TurretBuilding` | ranged weapons |
| `Task_MeleeAttack`, `Task_FocusedMeleeAttack`, `Task_RangedAttack`, `Task_UseTurret`, `Task_EquipBestWeapon`, `Task_SheatheWeapon`, `Task_StripWeapons`, `Task_CuffTarget`, `Task_MakeTargetStandStill` | AI tasks ([ai-tasks.md](ai-tasks.md#tasktype-to-task-class), [ai.md](ai.md)) |

### Global tuning: the CONSTANTS record

Loaded by `14086b2b0` (from "Initialising GameData", `140870ae0`) into one struct at `142133dd0`, field n at
`+4n`. **Verified** (field names read by the loader against the CONSTANTS record dumped from `gamedata.base`
through a probe, every value in the table below re-read; the "stored" rescalings re-read in the loader; consumers
by data xrefs and by the offsets used in the consuming functions). The loader rescales some values; "stored"
below is what the engine uses.

| Field | File value | Stored / use |
|---|---|---|
| damage multiplier | 0.65 | multiplied into the stored blunt/cut 1/99, pierce multiplier and stumble max |
| blunt damage 1 / 99 | 20 / 80 | blunt base at skill 1 and 99 |
| cut damage 1 / 99 | 20 / 80 | cut base likewise |
| pierce damage multiplier | 1.2 | |
| bow damage 1 / 99 | 1.1 / 1.3 | stored unscaled at fields 8 and 9; `14043c180` scales a crossbow's level-0 pierce ends by `bow damage 1` and its level-1 ends by `bow damage 99` before the quality lerp ([Ranged and turrets](#ranged-and-turrets), **Observed** in the disassembly) |
| stumble damage max | 80 | stumble threshold |
| unarmed damage mult | 0.8 | |
| attack chance factor | 0.05 | hit roll skew |
| base block chance | 70 | |
| block chance increase / reduction per 10 levels | 12 / 15 | stored ×0.1, i.e. 1.2 and 1.5 per level |
| max num attack slots | 1 | enemies that may melee one target (+ RACE `extra attack slots`) |
| damage resistance min / max | -0.65 / 0.65 | toughness resistance range |
| damage resistance randomised | False | present in the file only: the loader never reads it (no such string in the executable) |
| knockout mult 1 / 99 | 3 / 0.75 | KO time scale by toughness |
| knockout time base | 20 | |
| min / max toughness ko point | 10 / 85 | |
| degeneration mult 1 / 99 | 1.7 / 0.03 | wound deterioration by toughness |
| bodypart degeneration rate | 1.5 | |
| immediate blood loss, extra blood loss from bodyparts | 0.2, 1 | |
| bleed rate, bleeding clot rate, blood recovery rate | 0.01, 0.0085, 0.4 | stored ×0.1 |
| stun recovery rate | 1 | |
| blunt permanent organ damage | 0 | fraction of blunt that becomes lasting flesh damage |
| exp gain multiplier | 3 | stored ×0.25 = 0.75 |
| skill diff xp 2x bonus / 0x penalty | 10 / 25 | |
| xp rate toughness, strength, from walking, athletics | 1.33, 0.5, 0.5, 1 | athletics stored ×0.5 |
| XP rate medic 1 / 99 | 15 / 150 | |
| weight strength diff 1x / max, min strength xp mult | 20 / 1, 0.1 | strength XP from carrying |

### Objects

| Object | What | Where |
|---|---|---|
| `GunClass` | the firing data of any gun (29 virtual slots); base of the next two | loader `FUN_14043baa0` |
| `GunClassPersonal` | a crossbow's gun, owned by the `Crossbow` item | loader `FUN_14043c180`, made by the item constructor `FUN_140898c90` |
| `GunClassTurret` | a turret building's gun (GUN_DATA; source file `rangedCombat.cpp`) | `FUN_14043cdb0` |
| ranged controller | one per character (`Character +0x2f0`): skill, target, aim point, reload timer, fire flags | tick `FUN_140438c70`, fire gate `FUN_14043aee0`, reload `FUN_140437ce0`, target choice `FUN_140437f40` / `FUN_1404374d0` |
| `Harpoon` | one bolt or harpoon in flight or stuck: a plain struct, pooled by `HarpoonManager` | spawn `FUN_14043a2f0`, per-frame update `FUN_14043afe0`, flight `FUN_1404380e0`, hit `FUN_140439c90`, landed `FUN_14043acf0`, recycle `FUN_140436c10` |

### Record fields

**Base-game data** (**Verified** by reading `gamedata.base`, `Newwworld.mod`, `Dialogue.mod` and `rebirth.mod`; the counts agree with "Data facts" below): 7 CROSSBOW,
6 GUN_DATA. Every crossbow has `num shots` 1, `aim speed` 1 and the three target multipliers 1.

| Crossbow | pierce min 0/1 | pierce max 0/1 | range 0/1 | reload min 0/1 | reload max 0/1 | deviation 0/1 | perfect skill | shot speed 0/1 | ammo (charges) |
|---|---|---|---|---|---|---|---|---|---|
| Ranger | 20/30 | 30/45 | 600/800 | 3/2 | 4.5/4 | 5/3 | 60 | 400/500 | Bolts [Regulars] (30) |
| Eagle's Cross | 50/60 | 70/100 | 1500/1900 | 5/4 | 10/8 | 2.5/1.5 | 80 | 700/800 | Bolts [Longs] (10) |
| Tooth Pick | 10/15 | 20/25 | 375/500 | 2/1.25 | 3/3 | 11/6 | 90 | 350/450 | Bolts [Toothpicks] (40) |
| Junkbow | 10/15 | 20/24 | 300/400 | 3/2 | 4/3 | 12/6 | 90 | 350/450 | Bolts [Toothpicks] (40) |
| Oldworld Bow MkI | 20/35 | 35/50 | 800/1100 | 3.5/2.5 | 4.5/4 | 4/2.5 | 70 | 500/600 | Bolts [Regulars] (30) |
| Oldworld Bow MkII | 35/45 | 45/60 | 800/1200 | 4/2.2 | 8/6.5 | 4/2.2 | 80 | 500/600 | Bolts [Regulars] (30) |
| Spring Bat | 30/50 | 40/80 | 400/550 | 4.5/3.5 | 6/8 | 8/4 | 70 | 500/600 | Bolts [Heavies] (20) |

### Bolt visuals

**Observed** (decompile; `Harpoon` +0x70 state 0 recycle, 1 flying, 2 hit a character, 3 landed): each bolt is one hardware-instanced entity of the
CROSSBOW `live ammo` mesh (one `HarpoonManager_<mesh>` per mesh) with the gun's material, its local +z along the flight (`setDirection(dir, TS_WORLD,
UNIT_Z)`), scaled (1, 1, max(1, speed x 0.005)) in flight as a fake motion blur and 1 once it hits; no trail. A bolt past its range pitches down 8°
a second about world x and is recycled at range -5000. Landed (`FUN_14043acf0`): exactly at the hit point, flight orientation kept, a world node;
terrain hits under |dot(normal, dir)| 0.1 vanish; objects and buildings land with the "Stone" impact switch; above `harpoonLimit` (60) the landed bolt
with the longest flight time goes. In a character (`FUN_1406508d0` -> wound record -> `FUN_14052bfa0`): every hitting bolt sticks, attached to the
struck part's `bone name` (or `bone name 2` half the time, `FUN_140649fd0`; the root without the bone) at 0.6 x a random vertex of the body on the
bolt's side (back when it flew the way the character faces), keeping its world orientation in the bone's frame; no timer, cap or `harpoonLimit`;
all go at once when first aid leaves no wound (`FUN_14064d800` -> `FUN_14052bc10`) or the appearance reloads; not recoverable. Meitou: the debris
renderer draws them (in flight interpolated between ticks); a stuck bolt sits 2 units back along its flight from its bone's origin (engine choice for
the random vertex), not saved.

### Shot origin and flight collision

**Observed** (decompile and disassembly). Origin: the fire gate (`FUN_14043aee0`) refreshes the gun (slot 2 `FUN_1404351f0`) from the crossbow's scene
node in the shooter's `hands` (`Bip01 Prop2` with the attach offset; without built meshes the character's position + (0, 10, 0)); the muzzle (slot 9
`FUN_140434fc0`) is that node's world position + its orientation x (`barrel pos Y`, `barrel pos Z`, 0), the place of loaded bolt 0 (base game: (-1, 5.9, 0),
Spring Bat (-1, 1.75, 0)). The direction is normalise(aim point - muzzle) (slot 12), the aim point the target's `Bip01 Spine2` (`Spine1` for some targets)
plus a velocity lead whose form is **Unknown**, tilted by `randomDeviant(random x deviation)`. Flight: each frame a ray from the bolt along its z on the physics
thread (raycastClosestShape, group mask `0x087fde47`, read one frame late); a hit counts within 2 x the step. Groups tested: 0 and 1 (characters among them),
2 terrain, 6 and 14 foliage, 9-12 building floors, 15-18 stairs, 19-22 furniture, 27 unwalkable roofs; not ragdolls, items, doors, interior hulls or
passable parts. The shooter is skipped (`FUN_1400cd0b0`). Meitou: the simulation fires from a fixed point 15 up and 8 ahead (`RangedTuning.Barrel*`, measured
from the posed muzzle) and tests the terrain and the buildings' collision (`SolidHits`; no foliage); the renderer draws a new bolt from the shooter's posed
muzzle and closes the gap to its simulated flight over 150 units (`GameHost.LaunchBlend`).

### Arms

**Observed** (decompile and disassembly). The medical object (Character +0x458) holds two arm-usable bytes, right +0x5bd and left +0x5be, set every tick
(`FUN_140649320`): an arm is out when a part whose `collapse part` has its bit (2 right, 4 left) is a stump or crushed, or is down (flesh - stun + rig < 0;
`FUN_140644450`); an original or fitted prosthetic limb passes. Planner conditions 38 `hasOneWorkingArm` (either) and 161 `hasTwoWorkingArm` (both;
`FUN_140998470`, `FUN_1409984d0`). Weapon categories 3 Heavy, 6 Bow (every crossbow, `FUN_140898c90`) and 8 Polearms need both arms; the rest only the right
(`FUN_1409991d0`, weapon choice `FUN_1405c97a0`). Without both, the shooting gate (`FUN_1407f2840`) fails: attack orders stay melee (`FUN_1407f9e20`) and the
Shooting tasks 262/263/266 (condition 161) are not planned; an arm's change puts the held weapon away (`FUN_1405cc230`): a two-arm one on either arm, another
when an arm it needs is out. The melee weapon drawn (`FUN_140345c10`; hip and back are the first item of the `hip` and `back` sections, `FUN_140747c30`,
`FUN_140747e00`): right arm out, none (fists); right only, the hip one; both, the back one unless it is a crossbow, else the hip one. The hip one replaces a drawn
back one only when it rates higher by Item vtable +0x378, which returns 0 for every WEAPON and CROSSBOW (`FUN_1408a52d0`), so never. **Observed**, not built:
with both arms, `FUN_140334250` keeps the weapon held instead of drawing the back one; it is true when the character's location resolves to a building whose
+0xc2 byte is 0 (indoors?). Meitou: `MedicalState.ArmWorks` (also carrying and swimming); `Fighter.Weapon` takes back or hip by the arms each tick
(`Fighter.SetArms` in the combat Act, a change redraws), the appearance gives the first hip and back weapons both mesh sets (`WeaponShown.Back*`, snapshot
`BackDrawn`); the crossbow needs both arms (`CombatSystem` ranged switch) and the planner conditions read them. A two-arm category at the hip with one arm is not
traced.

### Unknowns of the ranged path

The ranged-or-melee switch; what sets the trigger flag (the time between the decision and the shot); the reload animation against the timer; a character's hit shape
(capsule or ragdoll) and whether downed bodies stop bolts; whether the crossbow's bleed and target multipliers and the armour's `ranged skill mult` do anything; turret
target choice and states; how NPC crossbowmen get their bolts.

## Unknowns

- KO wake-up, flags `+0x162`/`+0x163`, and the roles of hunger and "fed" in recovery; (the `+0x60`/`+0x64` pair is the hunger level and stomach buffer, [character-stats.md](character-stats.md#hunger-and-starvation)); where the KO point (`140643ae0`) is consumed.
- Attacker-side technique choice (which technique an attacker starts, using min/max skill, encumbrance, distance); whether `140887970` tests min/max skill. Attack-slot AI (`14060a9e0` and neighbours), aggression,
  stance and target selection. The bluntBase of non-blunt-dominant weapons; the stat that scales max blood; the part `+0x28` link.
- The role of the class-table columns 4-6 (athletics, combat speed, stealth is an inference) and of the slot-adjusted factor (armour item field index 0x5d) in `140898e90`.
- Turrets (target choice and states; see [Ranged and turrets](#ranged-and-turrets) for what is known and its own unknowns); the meaning of the weight/price-like armour table fields; the remaining difficulty floats beyond the dialog bindings listed above (their consumers).
- Whether the +20 guarding state of the block chance is the state the HUD's Block stance sets ([ui-screens.md](ui-screens.md#4-hud-mainbargui)).
- What `anim hesitate point` (stored as a frame fraction, no reader found) and `max encumbrance` do; what reads the stumble's `+0x128` = 0.85; where `min cut damage mult` x the manufacturer's `min cut damage` is applied (the engine floors the cut at it); the term shapes of the fist damage (the engine takes half the stat each). The engine's answers are in [simulation.md](../simulation.md#combat).
- Whether the decompiled maths matches in-game numbers: nothing has been compared in play.

## Expression reference

- Combat: `lerp(t, a, b) = a + (b - a)·t`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Turrets (secondary; **Observed** from `GunClassTurret` and the tooltip `FUN_14030d620`)
- AI (**Observed**)
