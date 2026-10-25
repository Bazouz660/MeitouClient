# Character animation

Original AnimationClass/race records, Ogre skeletons and body-shape binary research. Tables and
expressions retain scopes; [skeleton](formats/ogre-skeleton.md), [characters](characters.md),
[stats](game/character-stats.md), [combat](game/combat.md) own related facts. Runtime AnimationLibrary/
AnimationMask/AnimationSystem and CharacterShape use data; renderer consumes named layers.

## Clips and layers

Race/animation-source skeleton links supply clips, definitions and masks; normalize names and
attach to the intended skeleton rather than choosing a clip from another race by name alone.
Startup preprocessing, priorities/selection flags, movement speed, combat footwork, root-motion,
turn/head-look, hit/KO/get-up and breathing/wound overlays have different consumers. Keep the
original lower/upper/whole-body layering and additive/average semantics; do not translate every
clip as a whole-body override. Advance one-shots through fades; loop durations and root tracks
must agree with movement. Missing clips/lengths use explicit fallback, not original-parity claims.

Hit reactions: the six ANIMATION records with `stumbles` all relocate; their `Bip01` track is the knockback
(**Observed**, root displacement of the base-game clips in world units): `mid blow light` 1.13 s ~10 back,
`mid blow` 4.63 s peak 18 back (~45%) ending 4 back, `back blow light` 0.67 s ~0, `back blow low` 1.47 s 17.7
forward, `mid blow drop` 6.57 s ~4 back, `back blow high` has no clip. When they play: [combat.md](game/combat.md#hit-reactions).

### Choosing candidates (`@ 14051d660`)

Verified (decompilation). Every selection (movement blend, idles, stumbles) runs the same filter over a list
of definitions and gives each a weight; zero-weight ones are dropped:

- `category` must equal the character's current category (CharacterAnimCategory: 0 ANIM_NORMAL,
  1 IMPRISONED, 2 SLEEPING, 3 CARRIED, 4 SWIMMING, 5 GROUNDED, 6 COMBAT, 7 ATTACKS, 8 RANGED, enum names
  from the FCS editor). Meitou plays category 4 in deep water ([water states](game/pathfinding.md#water-states)): `swim` (clip `swim2`), `swim carry`, `swim carry left`, `swim idle`;
  and category 5 while crawling ([forced crawl](game/ragdolls.md#forced-crawl)).
- `is combat mode` and `stealth mode`: EITHER (2) or equal to the character's combat / stealth state (when the combat flag is set: [combat](game/combat.md#combat-stance)).
- Arms (`@ 14051d240`): an upper or overlay animation must fit what the hands hold (`has weapon L/R`
  against the character's state); details not decoded.
- **Speed weight** (`@ 14051d100`): with the current speed v (the character's forward speed, signed, below)
  and the definition's speeds times the race's scale g (vtable 0x390 on the character, 1 for humans):
  a ramp `r(v)` = 0 below `min speed`; rising linearly from 0 at `min speed` to 1 at `move speed` (just 1 when
  `min speed` < 1); falling linearly from 1 at `move speed` to 0 at `max speed`, 0 above; when `max speed` < 0.5 the
  falling line is followed past `max speed` too (`@ 14051cd10`): with a max of 0 it rises, so the fastest clip stays 1,
  and with a negative max (the backing-off `walk lower combat shuffle long BK`, −99/−10/−6) it is 0 at forward speeds. If both v and `min speed` are below −40 the weight
  is 1. A positive r is then sharpened: **w = 1 / (1 + e^(−10 (2r − 1)))** (so r = 0.5 → 0.5, r = 1 → 0.99995).
- **Leg weight**: the same ramp (without the sigmoid) over the health of the *more hurt* leg against
  `L/R leg damage min / ideal / max` of that side (left values if the left leg is worse). The loader forces
  min < 1 to −101 and max > 99 to 101, so a healthy-leg clip covers the whole range.
- Only for idle picks (layer upper or overlay): `being carried` / `carrying left` / `carrying right` must
  match the character, and the weapon-type flags must include the equipped weapon's type; for unarmed (type 5)
  a clip that `has weapon L` or `R` YES gets weight 0.1 instead.
- Finally the weights are **normalised per layer** (each layer's weights divided by that layer's sum,
  `@ 14051cde0`).

### Side steps

**Observed** (decompilation: `CombatMovementController::combatMovementUpdate` `@ 1402af1e0`, `combatMovementAnimationUpdate` `@ 1402ae1b0`,
`setupCombatMovementVariables` `@ 1402ae6d0`, `AnimationClass` slot 29 `@ 14051de70`, `@ 14051f3f0`; constants read from the binary).

- **Facing.** In the fight steering a fighter faces its target (`lookAtCharacter`) whichever way it moves, except in jog mode (body gap over 90,
  back under 70: [combat stance](game/combat.md#combat-stance)) or chase mode, where it faces its velocity.
- **State** `strafing` (+0x124): with the velocity in the facing's frame (z forward, x side), it starts when |x| / |z| > FORWARD_THRESHOLD
  (+0xf4 = 1.5 + 3r per character) and stops when |z| / |x| > STRAFE_THRESHOLD (+0xf0 = 1.5 + 2r), only when `strafeStateTimer` (+0x120) has run
  out; each flip sets it to 1 s. `canStrafe` (+0x126) is set to 1 at movement setup and never cleared (by the writes found); without it a strafing
  fighter would face its velocity. The steering also seems to keep only the velocity's side part while strafing and its forward part otherwise
  (decompile unclear; Meitou does not).
- **Animation.** Forward speed = z, side speed = −x (to the right) only while strafing, else 0; x is zeroed when |z| > 1 and |x| < 0.5. The side-step
  clip gets weight |side| / (|side| + |forward|) (the last value while the sum is ≤ 1, through a filter `@ AC+0x2b8`), the forward movement clips
  the rest; it plays at F × `play speed` × side speed (signed). Base data: `strafe lower` (layer lower, no `move speed`, rebirth.mod `play speed`
  −0.12, `strafe speed max` 999, combat only); `sidestep` is disabled and `strafe upper` removed. Which test reads `strafe speed max` (999 on
  the blows, 99 on the guards, 0 on the walks) is **Unknown**. Animals' `strafe anim` is not decoded.
- Meitou: `AnimationSystem.Strafe` and `AnimationLibrary.Strafe`; a fighter in its footwork (`CharacterCold.FaceSlot`) keeps facing its
  target while its path moves it (`MovementSystem`, the turn's speed cut skipped).

### Idle stance

Verified (decompilation). A human's animation object keeps its own idle: the `idle stance` of its appearance looked up by record name
(`@ 1405bd240` at creation, `@ 1405b9880` after an appearance change). Its idle pick (`AnimationClassHuman` slot 25, `@ 14051f230`;
animals' slot 25 is the plain draw) returns that stance when the character's category is normal (0), the record's `is combat mode`
and `stealth mode` are EITHER or equal to the character's, and the arm check `@ 14051d240` passes (it always does for a record
whose layer is not upper or overlay); otherwise it draws from the idle list (`@ 14051ec80`, `@ 140523450`). The standing update
`@ 14051d3b0` re-picks when the timer (`idle time` seconds) runs out, so a fitting stance is held. Weapon kinds are not tested.
Base data (**Verified** by probe): the 8 stances are layer `all`, combat and stealth mode 0, `play speed` 0 (`squat` 0.01), clips 0.033 s:
characters stand still in their stance out of combat and not sneaking. Meitou: `AnimationDefinition.FitsAsStance`,
`AnimationSystem.OwnStance`; a clip that never advances starts at its first frame (engine choice; the original's start time is **Unknown**).

## Shape notation and factors

**Verified formula body** from14052e6c0; stat-field identity matching remains **Observed**.
Sliders*0.01; lerp(a,t)=1+(a-1)*t, vector multiplication component-wise, bone-local XYZ.
M=(max(strength,weapon-smith,armour-smith)/100-0.2)/0.79, unclamped. Starvation
S=clamp((1-clamp(N-1,0,1))*1.5,0,1); original N identity was unresolved in this trace.
thin7/86/6/4=lerp(0.7/0.86/0.6/0.4,S); bulk=lerp(1.27 men/1.24 women,M),
broad=lerp(1.13 men/1.12 women,M). h=Height*g, Fr=Frame*g, L=Leg-length, H=h+L-1;
human g1, animal override unverified. LB=Legs-bulk*thin7*bulk, St=Stomach*thin6,
C=Chest*broad*thin86, c45=1+0.45*(C-1), c9=1+0.9*(C-1), A=Arm-bulk*thin7*bulk,
Sh=Shoulders*broad, F=Feet*h. Table retains every bone/positional vector and remaining local symbols.
Neutral vectors1 except thighY0.95 (CharacterShapeTests).

Limb factor0 only for a stump (limb state1; a prosthetic keeps1), else1; indices0 left-arm,1 right,2 left-leg,3 right-leg. Upper-arm
stump remains unless hide-stump bits1/2 remove it. Thigh n0.8 when that leg is a stump (limb state 1; **Observed**).
Calf k uses the signed leg ratio (`@ read_boot_height_n7`, **Observed**): b = worn boots' `boot height`, L/R = the `offset` of a fitted
left/right leg LIMB_REPLACEMENT (slot 52/53). Left only: (b+4)/(L+4); right only: x=R, y=b; both: x=R, y=L, then x<y gives
(x+4)/(y+4), else -(y+4)/(x+4), equal 0. Positive scales the left calf, negative the right, 0 neither (`CharacterShape.LegRatioOf`). Movement translation scaleH unless two unidentified states.
Muscle normal blends, head poses and skinning follow their own data/shader rules, not bone scale.

**Engine choice** GroundOffset compensates lowest foot/toe bind-pose height change per appearance;
it does not re-ground crouches or prove the original controller's mechanism. Physically moving
characters/root collision and leg-health/lying/care selection belong in [simulation](simulation.md#animation).

### Stat-driven body

**Verified** (decompilation): `FUN_14052bc70` sets three values on the human appearance (+0x1a0..+0x1a8) from the character's `CharStats`
(offsets: [character-stats](game/character-stats.md#the-stat-list)) and hunger (`+0x4b8`, [ui-screens](game/ui-screens.md)):

- **Bulk M** = map(max(strength, weapon smith, armour smith) × 0.01, 0.2 → 0, 0.99 → 1), not clamped. Strength is read through the stats object's slot-2
  accessor; Meitou takes the stat itself.
- **Definition D** = map((swimming + athletics + 3 × max(dexterity × x, martial arts) − cooking − science) × 0.01 / 5, 0.2 → 0, 0.9 men / 0.99 women → 1),
  clamped 0..1. x is the float at `CharStats +0x34` (**Unknown**; Meitou 1).
- **Starvation S** = clamp((1 − clamp(hunger − 1, 0, 1)) × 1.5, 0, 1).

`FUN_140531820` (not for robot races) then sets the body material's `normalMap` and `bodyBlendNormal` and its `muscleBlend` (`character.hlsl`:
`bodyN = lerp(bodyN, blendNormalMap, muscleBlend)`, before the head's normal is added), and, for a character with stats, rebuilds the bone shape
(`FUN_14052e6c0`, M and S above). The maps: base `nm <g>` blended to `nm <g> strong` by D; when S > 0.25 or S > D, blended to `nm <g> skinny`
by S, the base being `nm <g> strong` when D > 0.33 (RACE fields; humans and Shek have their own `_muscular` / `_skinny` files, robots and hive
one file for all). What calls it, and how often, is **Unknown** (it is a virtual of `AppearanceHuman`, slots 6 and 13).
Meitou (`CharacterShape.BuildFromStats`, `CharacterSnapshot.Build`, `CharacterRenderer.ApplyBuild`): published every tick from the live stats,
applied when a value moves by more than 0.01, the normal maps once both are resident (engine choices).

### Startup preprocessing

| Field | Override bones |
     | --- | --- |
     | `override L arm` | `Bip01 L UpperArm`, `Bip01 L Forearm`, `Bip01 L Hand`, `Bip01 L Clavicle`, `Bip01 Prop1` |
     | `override R arm` | `Bip01 R UpperArm`, `Bip01 R Forearm`, `Bip01 R Hand`, `Bip01 R Clavicle`, `Bip01 Prop2` |
     | `override head` | `Bip01 Neck`, `Bip01 Head` |

| Field | Tracks deleted |
     | --- | --- |
     | `delete root` | `Bip01` |
     | `delete L arm` | `Bip01 L Clavicle`, `Bip01 L UpperArm`, `Bip01 L Forearm`, `Bip01 L Hand` |
     | `delete R arm` | `Bip01 R Clavicle`, `Bip01 R UpperArm`, `Bip01 R Forearm`, `Bip01 R Hand` |
     | `delete below waist` | `Bip01`, `Bip01 Pelvis`, `Bip01 R/L Thigh`, `Bip01 R/L Calf`, `Bip01 R/L Foot`, `Bip01 R/L Toe0` |
     | `delete spine0` | `Bip01 Spine` |
     | `delete body head` | `Bip01 Spine`, `Bip01 Spine1`, `Bip01 Spine2`, `Bip01 Neck`, `Bip01 Head` |
     | `delete weapons` | `Bip01 Prop2`, `Bip01 Prop1` |
     | `delete tail` (default **true**) | `Bip01 Tail`, `Bip01 Tail1` ... `Bip01 Tail4` |

### Posture sliders

| Animation | Slider (appearance value) |
| --- | --- |
| `postures` | `Posture` |
| `neck set` | `Neck position` |
| `shoulder set` | `Shoulder set` |

**Observed** (`OgreSkeletonReader` inspection of `male_skeleton.skeleton` and `female_skeleton.skeleton`):
`postures` lasts about 0.166672 s; its 0.0333405 s key is neutral, corresponding to slider 20.
The renderer samples at `clip length × slider / 100`; generated NPC posture follows the
[appearance template](characters.md#random-appearance), rather than the editor range midpoint.

### The formulas (Verified, decompilation and disassembly of `@ 14052e6c0`; implemented in `CharacterShape`)

| Bone | Bone size | Positional size |
| --- | --- | --- |
| `Bip01 L/R Thigh` | (t, H × 0.95, t) × n, with t = (*Legs shape* × LB + (*Hips* − 1) / 3) × Fr, LB = *Legs bulk* × thin7 × bulk | (1, Fr × (2 − h) × *Hips*, 1) |
| `Bip01 L/R Calf` | ((2 − *Legs shape*) × LB × Fr, H, same) × leg factor × (1, k, 1) | – |
| `Bip01 Pelvis` | (*Hips* × Fr, h, *Hips* × Fr) | – |
| `Bip01 Spine` | (p × Fr, h, p × St × Fr), p = (*Hips* − 1) × 0.6 + 1, St = *Stomach* × thin6 | – |
| `Bip01 Spine1` | (*Waist* × thin7 × Fr, h, St × Fr) | – |
| `Bip01 Spine2` | (c45 × Fr, h, c9 × Fr), C = *Chest* × broad × thin86, c45 = (C − 1) × 0.45 + 1, c9 = (C − 1) × 0.9 + 1 | – |
| `L Boob`, `R Boob` (if the skeleton has `L Boob`) | (bx × Fr, by × h, B × Fr), B = *Breast size* × thin4; below 1: bx = (B − 1) × 0.75 + 1, by = (B − 1) × 0.5 + 1, else bx = by = B | (Bh, Bs × c9 × Fr × ((1 − h) × 0.5 + 1), (2 − h) × c9 × Fr); Bh, Bs = *Breast height*, *Breast spacing* for women, 1 for men |
| `Bip01 L/R UpperArm` | (A × Fr, h, ((A − 1) × 1.5 + 1) × Fr) × stump factor, A = *Arm bulk* × thin7 × bulk | (Sh × c45, 1, 1) |
| `Bip01 L/R Forearm` | (A × Fr, h, A × Fr) × arm factor | – |
| `Bip01 L/R Clavicle` | (Sh × Fr, ((Sh − 1) × 0.3 + 1) × Fr, Sh × Fr), Sh = *Shoulders* × broad | – |
| `Bip01 L/R Hand` | (A × Fr, h, A × Fr) × *Hands* × arm factor | – |
| `Bip01 L/R Foot` | (F, L, F) × leg factor, F = *Feet* × h | – |
| `Bip01 L/R Toe0` | (F, F, F) × leg factor | (1, 1, F) × leg factor |
| `Bip01 Neck` | (*Neck width* × thin6 × broad × Fr, *Neck length*, *Neck* × thin7 × Fr) | – |
| `Bip01 Head` | (f × *Head size* × *Head shape*, *Head size*, f × *Head size*), f = (Fr − 1) × 0.25 + 1 | – |
| `Bip01 Jaw` (if present) | (*Jaw* × head X, head Y, head Z) | – |

## Open questions

- The order of ANIMATION_FILE sources (comparator `@ 14052ba90`) and which file wins on a name clash.
- The suffix of the block-animation clones, and which COMBAT_TECHNIQUE `animal` values exist.
- The per-character blend rate and the normalising rules in detail (`@ 1405b5c30`).
- ~~Which record field turns on root-motion extraction~~: `relocates` for ANIMATION records; combat techniques
  by their own rule ([Root motion](#clips-and-layers)). Still open: the time divisor of the root-motion velocity,
  and how combat footwork moves the body while combat sets animation-driven movement.
- Per-bone call in `@ 140539020`: Kenshi calls one virtual method on every bone with false, and on the
  children of `Bip01 Head` except `Bip01 Jaw` with true. What it is (scale inheritance? manual control?) is
  Unknown.
- How characters choose between candidate animations: largely answered ([Choosing candidates](#choosing-candidates--14051d660),
  [Movement](#clips-and-layers), [Hit reactions](#clips-and-layers)). Still open: the
  non-combat lower-body update (slot 28 runs only in combat; the out-of-combat path, probably the same
  movement blend, was not traced), how the idle timer's units work, the arm check `@ 14051d240` in detail,
  the get-up clip's playback rate, how fast the movement direction turns, and which of `foetal` /
  `sitting dazed` / `knockout training` play when. Our engine's rules are in [simulation.md](simulation.md#animation).
- Body shape: the two
  movement-scale exceptions, the stats field x in the muscle definition, what reads (`Height` − 80) × 0.025,
  and what the game does when a body file lacks a slider (the viewer uses 100).

## Expression reference

- Selection data used by the simulation: `clip seconds per second = speed x play speed`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- The three layers each frame (`AnimationClass` vtable slot 6, `@ 1405b68c0`)
- Choosing candidates (`@ 14051d660`)
- Combat footwork and strafing (`AnimationClassHuman` slots 28 and 29, `@ 14051fc60`, `@ 14051de70`)
- The formulas (Verified, decompilation and disassembly of `@ 14052e6c0`; implemented in `CharacterShape`)
- Muscle definition and the body's normal maps (Verified, `@ 14052bc70`, `@ 140531820`)
