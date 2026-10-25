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
and injury variants.

**Verified** (loader `FUN_140888ca0`, record reader `FUN_1406684c0`): techniques not `disabled` go to an attack
list unless `is block` or `is dodge` (then the defence list), in data order. Each keeps a per-category table:
`katanas` 0, `sabre` 1, `blunt` 2, `heavy weapons` 3, `hackers` 4, `unarmed` 5, `bow` 6, `polearm` 8; with
`animal` > 8 only that category. `1 handed` is not read here (only by `FUN_1405ba280`). `power N` is stored ×0.01.

### Attack technique choice

**Observed** (`FUN_1402b4570` → `FUN_140888030` → `FUN_1408876d0`, limbs `FUN_140886030`):

- Gap = |aim − attacker| − 1.5·r·k − lerp(k, 0.45, 1)·r_t, at least 0. r, r_t: `pathfind footprint radius`; k: slot
  0x390 (1 for people). Aim: target + velocity × 1.2 s when its movement is plain mode 0, else its position.
- Pass: category = weapon's (5 for fists), skill = stored category level. If empty and not an animal: category 5 at
  `FUN_140884e00` = lerp(e, 0.2, 1) × (martial arts + CharStats +0x2c bonus − injury term) × slot 0x398. If still
  empty: that again, accepting hurt limbs. e = encumbrance factor (CharStats +0x190, 1 unburdened).
- Filter: category table; `min skill` ≤ skill ≤ `max skill`; (1 − e)·100 ≤ `max encumbrance`; `is prone` = posture ≠ 0;
  limb factor f > 0, and f ≥ 1 except in the last pass.
- f = product over blows. `limb` 4 (weapon) 1; 0 left arm, 1 right arm: 0.001 if not working (+0x5be / +0x5bd).
  Legs 2/3: prone → technique out; flag +0x5b8 or a leg part down → ×0.25. A limb part kind 1
  (`FUN_140643b30`, **Unknown** meaning) → 0.001. No blow above 0.01 → 0.
- Reach R = `attack distance` + L/2, L = weapon `length` (CharStats +0x200). Fists never armed: L = 0. The max R of
  filtered techniques, at least 1, is CharStats +0x150. A technique needs gap ≤ R.
- Weight: c = min(a, b)/max(a, b), a = gap + 3, b = k·(`attack distance` + L/4) + 3. Multiply by f, by 0.02 under
  `attack distance min vs static`, by 0.2 for the previous technique unless the target is in state 8, and by `chance`.
  Pick by cumulative weight (`FUN_1408a8c80`, `FUN_1409b1c80`).

Consequences (base data): "Cut left static" (−999) is never an attack. A never-armed person cannot use
"ma badpunch" (−4) and has no attack below martial arts 5. An armed fighter beyond every weapon reach may kick
or punch.

Meitou: `TechniqueChooser.ChooseAttack`. Engine readings: CharStats +0x2c is the gear's unarmed bonus; the injury
term, +0x5b8 and part kind 1 are not modelled. Leg down means a leg part's flesh − stun + rig < 0. Plain mode means
a target that is not fighting. Fists keep L = 0 (the original keeps the last weapon's length). The gap is 3D.

## Hit reactions

**Verified** (`FUN_1404391f0` from `FUN_1404394f0` on a hit): damage = cut+blunt+pierce+stun against the
stumble threshold. A stumble clip is picked unless the hit is light and one is already playing; it is big
only when heavy and none was playing; front/rear by a dot test (`FUN_140435600`). Humanoids only. "Heavy_Hit"
/ "Light_Hit" are only the Wwise "Impacts" switch (Heavy = a big clip). A picked clip cancels the defender's
own technique (its blow never lands) and holds it in the stumble state until the clip ends (`FUN_140665490`,
`FUN_14060cf60`, `FUN_140435570`); with no clip the technique is still cancelled. Every 1.2 s of the stumble
(`+0x124`) it may break into a block against a swing at 10–99% progress (block-chance roll `FUN_140885d80`).
Knockback is the clips' root motion only (**Observed**: no impulse in these functions; [animation](../animation.md); ragdolls: [ragdolls](ragdolls.md)).
A blocked blow neither staggers nor pushes (`FUN_140665540`). A shackled defender is knocked out instead 25% of the time; a defender whose fight is off: [stagger outside a fight](#fight-lifecycle-and-orders).

## Pacing

**Verified**: after an attack (clip ≥98% done) the fighter waits 0.6 s after a hit, 0.3 s blocked, 0.2 s dodged
(`FUN_14060a2f0`, `FUN_14060d6a0`; flat, no stats). Before swinging, an attacker in reach asks its target
(`FUN_14060c670` → `FUN_140666a30`): target swinging at it → the attacker blocks; target stumbling or blocking →
attack; otherwise an initiative roll on both attack skills (`FUN_140665370`, the skill roll above). If the asker wins,
the target blocks. If the target wins, the asker is refused; with no free slot it is refused too, and waits
(see [Attack slots and circling](#attack-slots-and-circling)). `max simultaneous hits` caps one blow's targets ([attack zone](#attack-zone));
`power N` multiplies hit N's packet; `limb N` picks the punch/kick sound.

**Observed** (decompiled, not measured in game), with several attackers: a target answering a request is asked by
anyone, not only its own target, and the winner of the roll turns to attack the asker (`FUN_140666a30`). Deciding
(`FUN_14060c8b0`) tries attacking first and defends only when that fails; a waiting fighter that is attacked goes
to deciding. During its own swing a fighter aborts to defend only while under 50% of it with an incoming blow past
50% (`FUN_14060a970` → `FUN_1406083d0(0.5, 0.5)`); while anyone attacks it the swing ends at the current blow's stop
frame (`FUN_14060a590`). The defence covers the most advanced incoming blow from anyone; a failed defence roll means
no defence for 1.2 s (+0x14c). A dodge plays its whole clip at its own speed, then decides at once; a block that
ends leaves 0.2 s.

**Verified** (decompile; the waits are `deadTimer` +0x148): there is no wait before the first swing (`startupState`, state 3, enters the
planned state at once). Deciding sets the wait to 0.5 s before it asks; the impact check overwrites it by outcome, so a swing that finds
nobody in its [attack zone](#attack-zone) waits 0.5 s, and the loser of the initiative roll (plan 1) decides again after at most 0.5 s.

### Attack zone

**Verified** (`calculateTargetsInAttackZone` `FUN_1406087a0`, `attackImpactCheck` `FUN_14060a2f0`; field names KenshiLib). At a blow's impact
frame the blow resolves against every character in a box ahead of the attacker, at most the technique's `max simultaneous hits` (+0x54), in
the list's order; each outcome sets the wait (the last wins) and each one's own block or dodge decides it (`Character::hitByMeleeAttack`,
HitMaterialType: `HIT_MISSED` dodged 0.2 s, `HIT_SWORD` blocked 0.3 s, flesh or metal hit 0.6 s).

- **Box**: heading f = the movement direction (`AbstractMovementBase` +0xd0, horizontal); k = the size (Character vfunc 0x390), s =
  lerp(k, 0.45, 1), r = `myRadiusX` (+0x1a0, the race's `pathfind footprint radius`), L = the weapon's length (CharStats +0x200). Centre
  (L/4 + r + max(0, attack distance − |pos − attack start|)) s ahead; half-extents (width, height, depth) (r + 0.5, 25, L/2) s, or
  (13, 8, L/2) s when the impact point's direction is `CUT_LEFT` or `CUT_RIGHT` (a horizontal cut sweeps wide and low). Then
  h = max(0, (offset − `getRadius`) / 2), `getRadius` = k × footprint: the centre moves back by h and the depth grows by h. The height
  follows the ground (a down ray, clamped to ±15 of the feet, +9). Physics overlap query, at most 20 bodies.
- **In it**: a character that is not an ally (`isAlly`), not unconscious (`isLiterallyUnconciousNotPretending`), and an enemy (`isEnemy`)
  or the current target; also a slave (slave state 1) that is not the attacker's ally, when the attacker's faction is not the player's (+0x250
  `isPlayer` null) and not anti-slavery (faction +8). Of the buildings in the box only walls (`WallBuilding`) and doors (`DoorStuff`) take the
  blow, f = 2 × blunt + 0.5 × cut of the powered packet: a door loses clamp(f, 20, 60) × 0.015 of its construction progress (Building +0x164,
  via `getBuildState`; a broken door nothing), a wall clamp(f / 2, 10, 60) × 0.003, destroyed at 0 (**Observed**).
- Per target the attacker frees that one's attack slot (`AttackSlotManager::freeSlotH`), and after a hit or block takes it off its own engaged
  list (`removeAttackerH`, [attack slots](#attack-slots-and-circling)).

Meitou (`CombatSystem.Strike`): the box above in 2D, a body counting when its size-scaled footprint circle meets it and its feet are within the
half-height + 9 of the attacker's; the order is the target first, then by distance (the physics order is arbitrary). Hits left over go to walls
(the nearest edge of the wall line within the half-width) and doors (the leaf widened by its half-width), nearest first (`ApplyZoneBuildings`);
a door takes it off its own progress as a bash does ([locks](locks.md#cutting-and-breaking-285--185-cut-286--186-break)). A defender that is not the attacker's target defends only with a block or dodge it is already playing.

### Attack slots and circling

**Observed** (decompiled). CombatClass state +0x1f0: 0 attacking, 1 defending, 3 deciding, 4 re-decide, 5 and 6 below,
8 stumbling, 9 no target, 10/11 chase. The planned next state is +0x1f4.

- **Engaged list** (+0x1f8, `FUN_140666e20`): an asker joins its target's list while the list holds at most 4,
  so 5 fighters at most. A refused asker adds to a two-frame overflow counter (+0x210/+0x214, swapped by
  `FUN_14060cc50`). `FUN_14060c670` joins before it checks reach. A blow that lands or is blocked takes its
  defender off the *attacker's* list (`FUN_14060a2f0` → `removeAttackerH` `FUN_1406657d0` on its own +0x1f8; not after a dodge, **Verified**). The list is cleared when the fight ends.
- **Slots** (+0x8, `FUN_140666310`): count = clamp(`max num attack slots` + RACE `extra attack slots`, 1, 5),
  `FUN_1406661d0`. It is 1 while the target is attacking (state 0) or when an **Unknown** test (+0x448 → +0x228) fails.
  The CombatClass constructor also clamps the CONSTANTS value to 1..5.
- A slot is granted by `FUN_140666a30` and kept until the swing's last blow (`FUN_14060a590` → `FUN_140665910`).
  Hitting someone removes them from your own slots.
- **The answer to an ask** (`FUN_140666a30`, CombatClass vtable +0x28). `FUN_14060c670` calls it on the *target's* CombatClass with the asker, the
  asker's fighting stat (`FUN_140885e40`) and its +0x14c timer; the return value is the asker's planned next state (+0x1f4): 0 attack, 1 defend,
  5 circle. **Observed**, in order: target's fight task not running (+0x130) → 0; target attacking (state 0, or deciding with plan 0) while
  `FUN_140665420` (its +0x140 value, read only when the asker's hand passes a test) is strictly between 0 and 1 → 1; no free slot
  (`FUN_140666310`) → 5; target stumbling (state 8, or deciding with plan 8) or `Character::stumbleState` `FUN_140435570` true (a stumble clip still playing) →
  **mass combat XP** to the target, take the slot again, +0x120 = 1 → 0; target without a target (+0x2a0 = 0xb) → plan 2, slot → 0 / 5;
  target AI object (+0x298) vfunc 0x10 true (**Unknown**) with +0x144 clear (+0x144 is set when the defence update `FUN_14060a190` →
  `FUN_140609c70` plays its block or dodge, and at a swing's last blow while attacked; cleared by the pause after an attack `FUN_14060d6a0`
  and the stumble; so a fighter already blocking or dodging falls through to the roll below): target defending → XP, slot → 0 / 5; target attacking → XP, then
  a random contest `FUN_140665370` of the two fighting stats (each side's factor 1 + |difference| × a constant for the lower one, two uniform
  draws; true → 5), else slot → +0x120 = 1, plan 1, → 0. Other cases: +0x240 set → target plans defence, → 0; target in Block mode
  (`CharStats::isDefensiveMode` `FUN_1402b2b50`) → 0; else the same contest: asker wins with a free slot → target plans defence, → 0; target wins → it plans to attack
  and turns on the asker (`CombatClass::setAttackTargetHandle` `FUN_140665650`, state 3 unless `FUN_1405b34b0`), → 1 when the asker is its target, else 5.
  `setAttackTargetHandle` does nothing when the asker already is its target (hand vfunc +8 is "equal"), else stores the asker's handle in +0x2a0..+0x2b0
  and notifies both characters ([being targeted](#fight-lifecycle-and-orders)). A focused fighter is turned this way too, but its next pick, at most 1 s
  later, returns the focused target.
- **Mass combat XP** (`FUN_1408c5e80`) therefore fires on the *target's* stats, once per ask answered in those three cases: being hit at while
  stumbling or already fighting someone. It adds exp gain multiplier × 0.05 to the save-only stat (no consumer found), so it changes nothing
  visible; Meitou does not call `XpService.MassCombat` (the stat is not read anywhere).
- **Target selection** (`CombatClassAI::chooseAttackTarget` `FUN_140666ee0`, from the deciding state; **Observed**). The current target is kept
  while the pick timer (+0x2f0, s) is under 1 (0.3 on Hold). Then a focused target (CombatClassAI +0x2c8, set by orders 5 and 61,
  [lifecycle](#fight-lifecycle-and-orders)) is returned without scoring, unless its path is in the task system's impossible list
  (`AITaskSytem::isPathImpossible`) or refocus mode (+0x2e8) is set. Otherwise three lists are scored: the attackers (engaged list +0x1f8) of
  the current AI task's target when the chooser is aware of it, the chooser's own attackers, and its threats (SensoryData +0x50). A candidate
  must not be crippled (vfunc 0x360) and must be sensed by the chooser or by the task's target. The best score over the lists wins; none →
  the sensed-enemy finder `FUN_14059fa70` when the task system's aggro mode (OrdersReceiver +0x30, KenshiLib `AggroType`, 0 aggressive by
  the constructor; no other writer traced) is 0, else no target.
- **Target score** (`AI::chooseBestAttackTarget` `FUN_14059e300`). Skips ragdolls (`Character::isRagdoll`), candidates the leave-alone test
  `FUN_1408569a0` passes, allies by faction (`FUN_1406b2630`, unless the candidate is a slave) and impossible-path candidates. Leave alone: for
  an NPC chooser, a candidate it does not remember as an attacker and whose faction is not an enemy, when the candidate has diplomatic status
  (`Character::isDiplomaticStatus`) or is in a war campaign whose +0xc0 is 0; for a player chooser, a candidate not aware of it, conscious,
  of another faction, while the chooser mans no turret, whose intended target is nobody, or who has no aggression towards it, or whose target
  is not the chooser's ally (and an obedient slave leaves alone its master's allies). So player characters never pick an unaware bystander. Weights are CharStats fields that `CharStats::init` sets
  to the same constants for every character (**Verified** literals; not saved, no other writer found; names KenshiLib 1ce4a16): `STUBBOURNESS`
  +0x154 = 5, `PROFESSIONALNESS` +0x158 = 1, `HOLDING_POSITION` +0x15c = 2, `IMMEDIACY` +0x160 = 2, `IMMEDIACY_MAX_RANGE` +0x164 = 200,
  `MANLINESS` +0x168 = 1, `MURDEROUS` +0x16c = −2. Terms, in order:
  - + stubbornness (×4 when the chooser's prone state, vfunc 0x368, is not 0) for the current target; − professionalness while the candidate
    staggers (its animation's current stumble clip, AnimationClass +0x228, the one `Character::stumbleState` plays);
  - − manliness × (candidate's attackers + waiting count, less 1 when the chooser is one); a Taunt candidate gets +2 and this term ×0.35; a
    Passive one −1;
  - +10 when the candidate's intended target (state broadcast +0x68) is the chooser's AI-task target, that target is not fighting it back, and
    the candidate has under 2 attackers;
  - a player-faction chooser drops candidates beyond 400 (its crossbow range with Ranged on); on Hold ×0.01 beyond 40;
  - when the Hold test `FUN_140598630` passes: + range term (×20 for a crippled chooser), + murderous (−2) for a candidate whose fight is off
    (then the whole score ×0.5 when its intended aggression, vfunc 0x1e8 `getIntendedAggression`, is ≤ 0), the whole score ×0.1 while it gets up
    (+0x278), ×3 for a turret gunner when the chooser is not an animal, −10 when crippled or limbless (`MedicalSystem::isUselessNoLimbGuy`);
    otherwise the candidate is dropped. A candidate whose path has failed (`AITaskSytem::isPathImpossible` `FUN_1405b0290`: the chooser's
    `pathFailures` map, AITaskSytem +0x218, where `notifyPathImpossible` `FUN_140796ed0` stores 20 when a path to it fails) skips these last
    terms and is never picked. (**Verified** order and factors in the decompile of `FUN_14059e300`.) Meitou: `PursuitSystem.PathImpossible`, a
    failed pursuit path leaves its target out of the choice (a player's pick too) for 20 s (the decay of the stored 20 is not traced; read as seconds).
  - Range term (`AI::scoreTargetRange`) = (1 − d/R) × w by the chooser's manoeuvre mode ([area](perception.md#the-attack-on-sight-tests)):
    HOLD_GROUND R = 2 (meiDistance + tolerance) = 46, STAY_CLOSE R = meiDistance + 200 + tolerance = 223, ROAM_FAR (the default) R = 200;
    w = immediacy in ROAM_FAR, else holding position (both 2).
  - So a person prefers, in order of size: the attacker of its protectee left unopposed (+10), its current target (+5, +20 crouched or
    crawling), one already fighting (+2 over an idle one), the nearest (up to +2), the least crowded (−1 per attacker). Meitou: `Combat.TargetScore`
    for the aggro look and the fighters' re-pick, candidates and omissions in [simulation](../simulation.md#combat).
- **Deciding** (`FUN_14060c8b0`): pick a target, set +0x148 = 0.5, then ask (`FUN_14060c670`). The ask requires
  centre distance − r_t ≤ +0x150 + (1.5r + L)·k and no other body nearer than r + 2.5 (`FUN_1402e9620`). That test
  (`FlockingTools::getDistanceToClosestCharacter`, called to count non-enemies only) takes the least `range` of the flocking list, 99 when empty;
  r is the race's raw `pathfind footprint radius` (CombatClass +0x280 = r + 2.5, ctor `FUN_14060a9e0`). The list (`FUN_14085a070`) holds every
  other character not down or dead (`FUN_1407d1440`) or imprisoned (+0x2f8) whose centre is within getRadius_self + getRadius_other + 40
  horizontally and 20 in height; `range` = the 3D centre distance, `isEnemy` = Character vfunc 1000 (**Observed**).
  If the ask fails: being attacked → defend; engaged → state 5; else state 6.
- **State 5** (`CircleStateAI`, update `FUN_1402b3180`): walk to the target until the body gap reaches max'
  (`FUN_14060cd20` = +0x150 + 1.5rk − L/4, +12 while running, minus 1.5rk; at least 6). Face the target and ask each
  tick. Once the ask passes and no other body is within 1.2 × (r + 2.5) (`FUN_1402e9620`), it re-picks its target (`FUN_140666ee0`,
  re-deciding on a new one) and, with a free slot, re-decides (**Verified**, `CircleStateAI` `FUN_1402b3180`).
  The attack start uses the same walk while no technique is in reach.
- **State 6** (`WaitingStateAI`, `FUN_1402b3760` / `FUN_1402b3800`): not engaged. Keep the body gap in
  [n/4 + 15, 3n + 25], each end minus 1.5rk. n = engaged count + last frame's refusals. Side-step at speed s
  (steering +0x114). Try to join each tick; re-decide once engaged or attacked.
- **Side-step speed**: drawn on entry as s = 50u − 25. If |s| is below the steering's least speed c (+0x108,
  `FUN_1402ae6d0`), s becomes ±12. c = 30 + 3b + 12u′ with b = 3 + 3u″. For a humanoid, c′ = 3(1 − e)c + 1 and
  b′ = 2(1 − e)b + 1. b′ is kept in 0..20; c′ is at most 90 and at least b′ + 10. So c′ = 11 for an unburdened
  person, 39 or more for an animal.
- Both states also start a 1.5–4.5 s timer. State 6 does not use it; state 5 uses it only in Block mode
  (CharStats +0x128, standing), clearing its own slots each time it runs out.

Meitou (`CombatSystem.HasSlot` / `MayJoin`, `PursuitSystem.Circle`):

- **Slots**: the holders are the fighters whose swing at the target has a blow to come. On the same tick, fighters
  with lower table slots ask first (engine choice for update order). Fighters are counted within 300 units. Those
  lower asks are foreseen from the last tick's state (threads run the fighters apart), counting any that may ask
  (rejoining, pause cut short by a blow at it, swing or block ending now, never asked yet), so the count is never
  exceeded. **Verified** by `DuelTests` (5 attackers, target fighting back, 1-3 slots).
- **Ask while defending**: a blocking or dodging target gets the initiative roll (the +0x144 path); one that wins while still in its
  defence leaves the loser yielding from the defence's end (engine choice; the original's `FUN_1405b34b0` test, which may cut the defence, is
  not decoded).
- **Player's pick**: a target the player picked (right click, Attack Target, Attack Unprovoked; not Attack All) is kept: the winner of a
  roll does not turn on the asker while that target stands (engine choice; without it a losing old foe re-yields every exchange and the
  order never sticks). The original lets the swap happen and returns to the focused target at the next pick (above).
- **Engaged list**: a landed or blocked blow takes a defender fighting the attacker off its list (`CombatSlot.Engaged`): it asks again.
- **Approach**: an engaged fighter walks in to body gap max(6, reach − L/4) and stands.
- **Joining**: a lower table slot asks first; one not yet in is counted ahead only when it may decide this tick (not staggered,
  defending, or swinging past its abort point), so nobody is left out for a fighter that does not ask (engine choice).
- **Circling**: only fighters left out when more than 5 attack one target circle, and not while their own swing plays (they
  ask at the next decision). They path to a point on the ring at least 1 s of
  side-stepping ahead (and beyond the walker's arrival radius plus one repath step, so a slow side-step does not creep), pushed out so the
  straight walk keeps to the ring, and face the target as the original does ([side steps](../animation.md#side-steps)).
- **Block-mode timer**: an engaged fighter on the Block order (standing) frees the slots on itself every 1.5–4.5 s (`CombatSlot.SlotsCleared`:
  swings started before hold none). The +12 while running is in `CombatTuning.StandGap`.
- **Body room** (`CombatSystem.NearestFriend`, `BodyRoom`): the ask fails while a non-enemy's centre is nearer than footprint + 2.5, ×1.2 in
  state 5 (`CombatSlot.CircleState`: engaged, its last ask failed with no blow coming at it). An AI rule, not collision: Meitou bodies are solid
  only to `BodyShare` × footprint (2.4 for a human), and the separation push (radius 8) spreads crowded allies until they have room. Engine
  readings: an enemy is one of a hostile faction, the target, or one fighting the asker; carried bodies do not count.

## Fight lifecycle and orders

**Observed** (decompile of kenshi_x64 1.0.68; class/method/field names KenshiLib 1ce4a16, **Observed**; literals **Verified** in the decompile).

- **Who has fight goals.** `Character::setupAI` gives every character goals 14 Idleness (priority 1) and 32 Self preservation (priority 3,
  urgent, target itself); non-player characters also 243 (animals 259). Attack on sight is goal 16 ([combat goals](ai-tasks.md#combat-goals)):
  NPCs get it from packages, a player character only while on Hold. Its desire (`AI::scoreAttackEnemies`) is 1, but 0 while sneaking outside a
  fight (`Character::isStealthMode`), Passive, or not standing (prone state ≠ 0); `scoreProtectAllies` is 0 when sneaking or Passive.
- **Being targeted.** Choosing a fight target (`CombatClass::setAttackTarget`, `setAttackTargetHandle`) calls `AI::underMeleeAttack` on the
  target at once (through `Character::attackingYou`), before any blow; dropping it sends "stop", which only removes the attacker from the
  target's engaged list. A landed blow (not blocked or dodged) on a defender whose fight is off sends it too (`FUN_1404394f0`). The call ends the
  target's sneaking, adds the attacker to its engaged list, to its senses (seen, alert timer 5) and to its squad memory as an attacker (kind 4,
  not for allies), tells a campaign, and when the target's fight is off: unless it is getting up, or is player-side and moving (CharMovement
  +0x24, `Character::isWithThePlayer`), its current goal is cleared and it replans (`AITaskSytem` vfunc 3); a target getting up instead has a
  10% chance of setting +0x4f8 = 10 (**Unknown**). The targeting path sets the call's last flag (r9b = 1 in the disassembly), the hit path
  clears it. With the flag, when the target is the player's, or the attacker is the player's and the target is not yet aware of it, the call
  is ignored unless both are in the same building and on the same floor and the target has a clear line to the attacker. So targeting a
  player's character through a wall, or an unaware NPC from another floor, alerts nobody until a blow lands. Meitou: `CombatSystem.Alerts`, answered by
  `RetaliationSystem` the same tick (it ends sneaking); same place by `SightMap.PlaceOf`, same floor as within 20 units of height inside, a terrain and
  building line between the eyes; aware = fighting the attacker or hit by it (engine readings).
- **Threats.** The perception pass (`SensoryData::addToThreatsAndFlockingList`) looks at each sensed live character of another faction: its
  state broadcast's intended aggression adds to +0x90; when its intended target (+0x68) passes `AITaskSytem::intendsToAttackTarget` (prone
  state < 3 and its fight on; on Hold with the fight off, only within 40), a target that is the looker puts it on the threats list
  (SensoryData +0x50) and adds its aggression towards the target to +0x88; a target of the looker's faction adds that to +0x8c.
  `iAmBeingMeleeAttacked general` (state 159) is "threats list not empty"; `immediate` (158) is Character +0x2b0, set in the periodic update
  (`FUN_1404378f0`) while a threat within 40 stands (prone state < 3), is not in a bed or cage, not getting up, not carried and fails
  `AI::isTargetOutsideTheGates` (not decoded).
- **Self-defence.** Self preservation's desire (AI slot 17): 1 with a threat within 40, 0.7 with a threat of positive aggression within 181.7
  (squared 33000), else 0. It wants `!isAggressionTowardsMe`, given by 13 "Attacking attackers of" (desire 1; finder `AI::findAttackerOf`:
  the subject's attackers and threats that are enemies or target it, never allies, within 150 of a subject other than the chooser; best by the
  target score) or 35 Running away (slot 55: 0 for a player character that can still fight: an arm to fight with
  (`MedicalSystem::hasAnArmToFightWith`), ranged mode, both arms, or a weapon outside the two-arm categories; else 1 when crippled; else 0.001
  when it can get out (`AI::isLocationAccessible`: outdoors, or its building has a door open or opening, or none: `Building::hasAnOpenDoor` `FUN_1405499a0`,
  **Verified**) unless it stands inside town walls whose gate state (`FUN_1402eb070`, a lookup keyed by a pair of ids, meaning **Unknown**) is 3, else 0). So an attacked character fights back unless crippled; Passive does not touch this path (exe tooltip: "Passive
  focuses on non-combat unless attacked first"). Meitou: [order toggles](../simulation.md#order-toggles).
- **Melee or shooting.** Row 4 Attacking (`AI::scoreMeleeAttack`, slot 53): 1, ×0.1 with Ranged on, ×0.5 when its squad is not intact (`Platoon::isIntact`);
  0 against a caged target; a crippled fighter 0.1 (0.0001 in Block or Hold with no threat within 40; 0 for a non-player non-unique); on Hold
  only for a target its range tests accept. Shooting 262/263/266 (slot 54): only with Ranged on, a crossbow at back or hip and its bolts carried:
  1 (0.5), 0.1 with a threat within 40, which their list B forbids anyway. So Ranged on shoots until dry or rushed, Ranged off never shoots.
- **Fight task.** Rows 4 and 227 build `Task_MeleeAttack`; orders 5 Attack Target and 61 Attack Unprovoked build `Task_FocusedMeleeAttack`.
  The planner first needs 37 `isReadyForAction`, given by 59 Get ready after 6 Equipping weapon draws (at once: its task swaps the
  weapon in one call, **Observed**; Meitou draws when the fight starts). Task start → `initCombatMode` (mode 0):
  CombatClass +0x130 on, state 4, attack start position, the technique cleared; the focused task also stores the target as CombatClassAI
  +0x2c8 and (with its +0x98 flag) marks the broadcast as aggressor (+0x114). Task end → `Character::endCombatMode` → mode 2: +0x130 off,
  the target released, state 4, dead timer `calculateDeadTime(2)` = 0.2 s, the animation combat flag off, movement mode MOVE_NORMAL
  (`CharMovement::setMovementMode`), engaged and
  threat counts zeroed, focus cleared. `getTimeSinceLastCombatModeInHours` reads +0x138, stamped at start.
- **Weapon away.** The fight's end sheathes nothing. Idleness (14), first aid, splinting, repair, lockpicking and patrols need
  `!hasWeaponEquipped`, so the planner puts 7 Sheathing weapon before them: a character stays armed while urgent goals run and sheathes when it
  drops to Idleness or is ordered to such a job.
- **Orders beat goals.** `AITaskSytem::chooseGoal` tries, in order: the player's order queue (`checkOrders`, OrdersReceiver +0x38), jobs, urgent
  perma-jobs, priority-3 goals (Self preservation, Hold's goal 16, package goals), the other perma-jobs, priority 2 then 1 goals, the hold-position
  task. So a character with an order does not defend itself: a hit staggers it (below) and the queue's first order then runs again.
  `Character::addOrder` without Shift clears the order queue and the jobs, sets the AI's centre of movement to the order
  point ([area](perception.md#the-attack-on-sight-tests)), queues the order at priority 3 and replans at once.
- **Right click elsewhere mid-fight.** Ground: `PlayerInterface::playerMove` gives each selected character order 29 Move order. At the replan
  its fight task ends (above): it no longer blocks or dodges, since a target whose fight is off always answers an ask with "attack"
  ([ask](#attack-slots-and-circling)). It also stops being its attackers' threat and leaves their engaged lists, so once their 1 s pick timer
  runs out they keep it only through the sensed-enemy fallback or a focused order, then chase it ([leaving](#leaving-a-fight)). Each blow
  that lands on it on the way staggers it (below). `Task_Move` start resets the clip and turns off root-motion tracking
  (`CharMovement::trackAnimationMovement`); it zeroes the Hold point (CharStats +0x130), which its update sets to the move's
  destination while on Hold, so Hold follows the last ground move. Once the order is done the goals run again: with a
  threat left, Self preservation restarts a fight on `findAttackerOf`'s pick; without one, Idleness sheathes. A right click on an enemy is
  order 5 (focused); on anything else, that target's quick verb.
- **Stagger outside a fight** (`Character::_startStumble` `FUN_1404391f0`, from every landed blow). A shackled defender is knocked out
  instead 25% of the time (`FUN_140644980` with factor 0.002, the leg-loss KO shape, so its 3 s floor). Otherwise the movement stops, and when the defender's fight is off: one
  getting up is left alone; else, unless it is shooting at a live target, its current task ends (`FUN_1400522c0`), and task 147
  `STUMBLE_TASK_FORCED` ("Stumbling") is pushed to the front of its order queue (`AI::forceOrderInsert`, which also replans).
  `Task_Stumble` tracks the clip's root motion until `Character::stumbleState` reports the clip done (`AnimationClass::isLinearAnimationDone`),
  then ends and is popped (`AITaskSytem::bodyTaskComplete` pops the front order when it is the finished task). The interrupted order stays
  queued behind it and is planned again from its start. The clip itself is picked only for a humanoid that is not a ragdoll, not KO'd or dead
  and standing (prone state 0), and only when none is playing or the blow is heavy ([hit reactions](#hit-reactions)); without one the
  Stumbling task ends on its first update. A fighting defender stays in its fight's stumble state instead.
- **Actions mid-fight.** Any order runs during a fight. First aid 25, splint 249, repair 60 and pick lock 76/201 test no threat in their rows
  or task updates, so they run while their character is attacked (after sheathing), but every landed blow cuts them off with a stagger and
  the order is planned again afterwards (walk, sheathe, act; what the action keeps of its progress is per task, not traced). The Loot order (26, `Task_Loot_Order` update) ends when a
  threat is within 40 (+0x2b0). Rows refused while a threat is within 40: Shooting, 146 Turret gunner,
  123/290 Bashing gate, 272 Smashing, 164 Following and talking; 248 Attacking Squad needs no threat at all. There is no pickpocket verb on a
  standing character: stealing from a person is Loot on one that is down or asleep ([verbs](ui-input.md#context-menu-verbs)), after a Stealth KO
  ([below](#stealth-knock-out)) or a knockout.

**Standing orders** (`Character::setStandingOrder`, player faction only; ids are KenshiLib's `StandingOrder`, `M_SET_ORDER_*`):

| Id | Order | Effect |
|---|---|---|
| 0, 1, 2, 16 | run, jog, walk, group speed | movement speed mode; a crouching (prone state 1), uncrippled character stands up instead |
| 3, 4 | stealth on / off | Character +0xd4 |
| 5, 6, 7 | aggressive / defensive / evasive | AI +0x2b8 (no reader found) |
| 8, 9 | far / near | manoeuvre mode: 8 ROAM_FAR (centre target cleared), 9 STAY_CLOSE (centre at its position) |
| 11 | Block (`DEFENSIVE_COMBAT`) | CharStats +0x128 (set only with vfunc 0x250, KenshiLib `isHuman`) |
| 12 | Hold | +0x12b; on: hold point +0x130 = position and goal 16 added (priority 3); off: goal 16 removed |
| 13 | Passive | +0x12c |
| 14 | Taunt | +0x12a |
| 15 | Jobs (`CHASE`) | `OrdersReceiver::setJobsEnabled` |
| 17 | Ranged | +0x129 |

Readers: Block (`isDefensiveMode`: +0x128 and prone state 0) feeds the block chance, melee defence and dodge (exe tooltip: never attacks,
+20 defence), makes the ask answer 0 and runs the state-5 timer. Hold (`holdPositionMode`: +0x12b, not sneaking) feeds the post test
`AI::isTargetInRangeForHOLDMode_hostiles` ([perception](perception.md#the-attack-on-sight-tests)), the score's ×0.01, the 0.3 s re-pick and
the melee desire. Passive and Taunt act through the desires and the target score above; Ranged through the shooting desire and the shooting
gate ([ranged](#unknowns-of-the-ranged-path)).


## Combat stance

**Observed** (static reading; the animation's combat flag is `AnimationClass +0x244`, the value a clip's `is combat mode` is compared with, [animation](../animation.md#choosing-candidates--14051d660)). One setter, `FUN_14051cc30`, writes it. Set on: every tick of the fight's attack update (`CombatClass::vfunc_11`), and by the combat footwork (`FUN_1402ae1b0`, from a movement flag, not from the target). Cleared by: any new movement goal (`CharMovement::vfunc_18`), the end of the fight task (`CombatClass::vfunc_2`, mode 2), and the chase branch of `CombatClassAI::vfunc_9` (fight states 10 and 11, which also put the movement back to its plain mode 0; every other state sets movement mode 1). When a movement stops (`CharMovement::vfunc_19`) the flag is set to whether the fight task is active (`FUN_140436610`, `CombatClass +0x130`, set at task start). So the stance follows the fight's own steering and is off while the character walks a path to a goal, including a chase of its target along a path (states 10/11). `CombatClassAI::vfunc_9` sets `+0x58` each tick when the navmesh line query between the two (`FUN_1403a56e0`, 1 clear, 0 blocked, −1 failed) returns 0; that, or the two being in different places (a vtable `0x60` id of each differs), or the 2 s chase timer `+0x14c` still running, picks the chase; otherwise the AI picks its target and approach (`FUN_14060c8b0`, state 3) and steers with mode 1.

**Observed** (static reading, the fight steering `FUN_1402af1e0`; its constants are literals in the decompile): the footwork flag `FUN_1402ae1b0` reads is a "running" flag. With gap = distance − (own radius + target's radius), it is set when gap > 90 (> 160 in a steering mode 6, not decoded) and cleared when gap < 70; then forced on while the target is fleeing (`FUN_1402ae0e0`: target's movement in plain mode 0, speed > 9, for 1.5 s on end; the chaser then aims at a lead point, below). The anim combat flag is `!running`. Speed: up to `min(cap, own max speed)` (the AI's cap is 200, so the max speed), ramping 15 units/s per second; not running it is capped at 18 (16 backing off). So a fighter runs normally from afar and drops into the shuffle within 70 units of its target, distance-driven, whether or not it walks a path.

**Observed** (static reading, `FUN_1402af1e0` → `FUN_1404354d0`; the stat from `FUN_1408866b0` → `FUN_140883a80`): the lead point is the target's position + its velocity (Character vfuncs 9 speed × 10 heading, movement +0xb8 and +0xd0) × `(1 − r) × 2 + 1.2` seconds, recomputed every tick, with r the chaser's CharStats +0x188: its attack play rate (lerp(0.01 (0.6 dex + 0.4 attack), 0.8, 1.2) × G, capped at 1.2, the rate attack clips play at; × 0.8 when `FUN_1405c7fd0(character) == 3`, condition not decoded). So 0.8 s ahead at r = 1.2, 1.6 s at 0.8, more for a slowed attacker; a faster chaser aims past the runner, and since the lead follows the velocity it swings to the new side when the runner turns, and drops as soon as the runner slows under 9 or leaves plain movement (the 1.5 s timer restarts). The steering faces its movement while leading. A second override (`CharMovement::vfunc_28` sets movement +0x168 and a point at +0x16c, the steering's +0x50/+0x54): the chaser heads for that point instead at most 55 u/s, the lead is off, and it clears within 10 units; `CombatClassAI::vfunc_9` skips its chase while it is set. Its caller is **Unknown**.

Meitou (`PursuitSystem`, engine rules): the lead is a path goal, re-pathed when it moves over 12 units (at most every 0.5 s, so a turn can overshoot by ~12 units at a run), put a further 0.5 s of the runner's way ahead (a path ends in a stop and a fighter's speed ramps back slowly) and `PassWidth` 1.25 × the two footprints (clear of the separation radius) beside the runner's track on the chaser's side (bodies are solid, so the chaser passes the runner instead of pushing it along; the original's runs through it). It is used only while the chaser is behind the nearer of the lead point and the swing's aim point (target + velocity × `AimLeadSeconds` 1.2); level with it, it goes for the target and stands once the aim point is within reach, so the runner comes into its swing. Standing is measured to that aim point, as the swing is (stood within reach of the runner itself, the aim point would be out of reach and the chaser idle). A fighter at its path's end asks for the next one after `RepathSeconds` 0.5 (not the wanderer's 3 to 10 s wait). A lead off the navmesh falls back to the target. The +0x168 override is not modelled.

Data (**Verified**, the base ANIMATION records through `AnimationLibrary`): with the flag on, the lower clips are the `walk lower combat shuffle` trio (speeds −99 to 50) and `jog lower combat fallback` (50 and up); `walk`/`jog`/`run lower` are combat NO.

**Unknown**: the extra offset the steering adds while leading in a non-zero steering mode (`FUN_1402e9420` at radius + 40, `FUN_1402e9530`) and the side terms of `FUN_1402e93c0`; whether the original also holds a chaser from swinging while behind; the steering's radii (`+0xe0` and `FUN_14065dc30`) and mode 6 (possibly a ranged closer; Meitou uses 90 for every fighter). When an idle character starts fighting back: [lifecycle](#fight-lifecycle-and-orders). Meitou: `RetaliationSystem` engages it at once, and `PursuitSystem` keeps the running flag by the rule above ([simulation](../simulation.md#animation)).

## Leaving a fight

How an attacker stops when its target runs. **Observed** (static reading of `Task_MeleeAttack` update `14034ca40`
(thunk `14001681f`), `ChaseMode` vfuncs 0/1 via thunks `140016243`/`140020d1a`, `CombatClassAI::vfunc_9`); literals
**Verified** (decompile). There is no distance leash: the exits are the target going down, nothing left to fight,
and the give-up test of a path chase. Ending the task means `FUN_1400522c0`: it sets the AI task system's `+0x26c`,
which its next update (`FUN_14050d920` → `FUN_14050d7d0`) consumes by dropping the current task and clearing its targets.

- **Target down or held**: each tick, a fight target that is down or dead (`FUN_1407d1440`) or imprisoned (`+0x2f8` 2)
  is dropped and the fight goes to state 4 (re-decide); when it was also the task's target the task ends.
- **Nothing left** (`Task_MeleeAttack` update): the task ends when the fight task stopped (`FUN_1405c9750`), or when all hold:
  the senses (Character vfunc `0x68`) count no enemies (`numEnemies` +0x94 = 0) and no threat to allies (`totalThreatLevelAllies` +0x8c ≤ 0,
  [threats](#fight-lifecycle-and-orders)), nobody is engaged on the character (engaged list + overflow 0),
  the fight has no target (`+0x2a0` none), and the task's own target is gone or fails its vfunc `0x30` test.
- **Path chase give-up** (`ChaseMode`, a base of `Task_MeleeAttack` at task `+0x78`): checked only while the fight is in
  state 11, the path chase that `CombatClassAI::vfunc_9` enters from state 10 when the navmesh line to the target is
  blocked, the two are in different places, or the 2 s chase timer runs ([Combat stance](#combat-stance)). Any other
  state clears the test's cached gate, so it restarts with each path chase. The gate (`vfunc_0`, computed once on entry,
  which also stamps the start time) passes when the chaser's faction is **not the player's** (Faction `+0x250` null),
  the target is alive (vfunc `0x30`), not in a bed or cage (`+0x2f8` 0), the chaser's Dialogue passes `FUN_140671c60`
  (**Unknown**), and either the target flees (its current task is 35 Running away or 127 Retreating fast, or 29 Move order
  to a point farther from the chaser than the target is: `FUN_140331ee0`), or the chaser counts it an enemy (Character
  slot 125), or two floats of the chaser's status object (`+0x1a0`: `+0xa0` + `+0xac` > 0) or the target's `+0x148` are set.
  Then each tick (`vfunc_1`, never when Character slot 126 calls the target friendly):
  - nothing before 5 game minutes since the chase began; after 1.1 game hours (sense-age unit > 60, below) the AI's plan is
    reset (`FUN_1405074a0`) each tick, the chase continues;
  - the chaser has no sense entry for the target → give up;
  - the target is neither seen nor heard (entry `+0x38`/`+0x39`) and its last sighting is older than 12 units, or the chaser
    stands within 60 of where it was last sensed (squared 3600) → give up.
  - Giving up ends the task, fires dialogue event 60 GIVE_UP_CHASE from the chaser at the target and resets the AI's plan.
- Time: the world clock is in game hours; the sense-age unit is hours × 3600 / 33 × 0.5 (`FUN_140852150`, `FUN_14033a170`),
  so 12 = 13.2 game minutes, about 24 s real at 1x ([game loop](game-loop.md#the-three-time-values)).
- So a player's character never gives up a path chase by this rule, and an NPC on open ground (no path chase) keeps
  running at the target with the footwork lead while it has a fight target. When that pick (`FUN_140666ee0`) and the attack
  goal's finder (seen/heard only, [Combat goals](ai-tasks.md#combat-goals)) let go of a runner on open ground is **Unknown**:
  how long a lost sense entry keeps counting as a candidate was not traced. The runner's side: [perception](perception.md#retreat-and-memory).

Meitou (`AggroSystem.Chase` / `SenseQuarry`): this give-up (9.1 s gate, 24 s unsensed, 60 units; NPCs only, event 60), tested in every
running chase, not only a path chase (engine choice), but never while a target not sneaking is within 60 of the chaser itself (engine choice: one running past its runner has it at its back); the chaser senses only its own target, a sneaker by sight without the spot meter.

## Armour and medical hit

Coverage rolls, slot order, armour class/material quality and cut/blunt resist/efficiency affect
packet before a weighted body-part hit. Armour absorption/penetration and cut-to-blunt conversion
are distinct; never collapse protection into one damage multiplier. Apply blunt/cut, wound,
stun, bleed and reaction through MedicalSystem, then XP. Health, bandage, stun, blood and
KO/coma/death thresholds use [character-stats](character-stats.md). Never tick medicine twice.

## Ranged and turrets

**Shoot At** (context-menu order 235, offered when the lead mans a turret; [ui-input](ui-input.md#context-menu-verbs)): Meitou's `ShootAtOrder` sets `CharacterCold.TurretTarget` on each selected gunner; the turret's target choice then returns that character while it lives and is free (not dead, caged or carried), instead of scoring candidates, and waits while it is out of range or blocked. The order ends when the gunner stops manning. Engine reading (**Unknown** how the original's 235 binds the target to the gunner's controller).

Controller phases reload → steady aim → fire; target motion/range, skill/injury, equipment and
line of sight determine timing/spread. Bolts retain launch/direction/velocity/impact/friendly-hit
state and consume ammo charges; do not model hitscan. Turret objects and mounted ammo/reload
branches differ from carried crossbows. Player/NPC targeting and friendly-fire training have
separate eligibility. The retained field/constant tables and expression list specify decoded
values; trajectory, aim timing and obstruction uncertainties remain labelled.

## Turrets

**Observed** (decompile of `kenshi_x64.exe`; functions cited) unless marked. A turret is fired through its gunner's ranged controller (`Character +0x2f0`), the same one a crossbow uses.

- **Building and gun.** `TurretBuilding` constructor `FUN_14043cdb0` builds a `GunClassTurret` from the BUILDING's `gun turret` GUN_DATA ("Error: turret … has no gun data"
  without one). The shared loader `FUN_14043baa0` reads `range`, `accuracy perfect skill`, `accuracy deviation at 0 skill`, `aim speed`, `shot speed` (≤ 0 → 1), `num shots`,
  `reload time min/max`, `barrel pos Y/Z`, `live ammo`, `reload anim`; loaded shots start at 0 (saved as `current shots`, slots 20/21). GUN_DATA has no damage and no `ammo`
  field (fcs.def `[CROSSBOW,GUN_DATA]` vs `[CROSSBOW]`), so a turret never runs dry.
- **Damage D** = the building's output value `+0x1a4` (**Verified**: loader `FUN_14029e460` = BUILDING `output rate` × functionality `production mult`, ÷ `max operators`
  when > 1; tooltip `FUN_14030d620` "Default damage:" shows Building slot 120 `FUN_140297b30` = `+0x1a4` × the research multiplier of a player-owned building (`FUN_14082ec70`,
  owner test `FUN_140546dd0`)). Base data: harpoon turrets 75, auto/multi 50 (`output rate`, **Verified** probe); walls 10–30.
- **Gun slots of `GunClassTurret`** (vtable `1416d5258`): 6 target weight `FUN_1404348e0`: 1 when dot(turret facing `+0x458`, target − turret) > 0, else 0.1 for a target outdoors,
  0 / 0.1 for one inside a building (by a handle and level test, **Unknown** detail); 12 aim direction = the barrel node's +Z (`FUN_140434f40`); 13 aim error = building `+0x448`;
  18 mount factor M = 1 + 0.01 × the output value of the building in `+0x478` (the `mounted` handle: a wall's turret bonus) (`FUN_1404353b0`); 19 power factor P = 0.8 e + 0.2 (`FUN_140435380`),
  e = building slot 109 `FUN_140296e00` = 0 when not operational, else hp ratio × power grant; 22/23 pierce ends trunc(0.75 D), trunc(1.25 D).
- **Manning** (`Task_UseTurret`, update `FUN_1403474f0`): the building of the order's target or the character's `+0x258`; it must be a turret (slot 107 = 8) with a free
  operator place (slot 159); the gunner faces it and holds clip `combatstance`; unless broken (`+0x3b6`) the controller gets the building and stat 11 Turrets
  (`FUN_140434570`, also sets it active), the controller ticks, and slot 150 `FUN_140434d20` registers the operator and sets the turning speed `+0x464` = e × `aim speed` × (1 + 0.01 D).
  Start/end (slots 1/3) set the building's in-use flag; the end clears the controller (`FUN_140437470`). Crossbows use the same controller with stat 35 (`Task_RangedAttack` `FUN_14033bfb0`).
- **Controller tick** (`FUN_140438c70`): sets the trigger flag `+0x34` every tick (so the fire gate `FUN_14043aee0`, run from the character update `FUN_1405cf130`, works every
  frame); chooses the target; none → state 3. Empty gun → state 2. In range (`FUN_140436860`: squared distance gunner→target < h × range², h the height factor from the gunner):
  XP perception and the stat (`FUN_1408c7250`: turrets 1.0, perception 3.0 per second), then the fire decision. With a turret it never walks to the target (blocked or out of range: wait).
- **Fire decision** (`FUN_140437ad0`): aim error ≤ 1°; the steady timer (0.4 s, reset after a shot or reload) counts down only then; a ray from the muzzle along the barrel
  (`FUN_140435b40`, mask `0x87f9e07`): a non-hostile character → hold; something nearer than the target → blocked; else fire.
- **Fire gate** each frame: the reload `FUN_140437ce0` (an empty gun reloads clamp(lerp(s, max, min)), s = 0.01 (0.6 skill + 0.4 dex); XP stat and dexterity while it runs;
  ammo only used when the gun has an ammo record); while loaded the aim point (target `Bip01 Spine2` + lead, `FUN_140436d10`) goes to the building (slot 5 → `+0x4a8`); with the fire flag, the shot.
- **Turning** (`TurretBuilding::vfunc_28` `140434b70`, `RotatingEnt::vfunc_2`, **Verified**): `+0x448` = −99, then while `+0x464` > 0 and someone operates, each `rotation function` 5
  part turns toward `+0x4a8` by its error (yaw about Y or pitch) limited to ± `+0x464` × frame time, down the part chain; `+0x448` = the largest error (degrees,
  `FUN_1404cc0d0`). So an unpowered turret does not turn and its error −99 passes the 1° test: it fires where it points. Sounds: [audio.md](../audio.md#other-world-side-events).
- **Shot** (`FUN_14043a730`): muzzle = barrel node + orientation × (0, `barrel pos Y`, `barrel pos Z`); direction = barrel +Z tilted by `randomDeviant(rand × max(0, lerp(0.005 (skill +
  perception), deviation, 0)))`; harpoon damage = U[min, max] × P × hM, range = P × range × hM, speed = shot speed × (0.8 P hM + 0.2), h = height factor from the muzzle; friendly
  avoidance min(1, 0.015 FF); `Attack` on the building's object; the target is told it is attacked (`FUN_140999d50`). The harpoon is a bolt of the gunner (hit `FUN_140439c90`, [Bolt visuals](#bolt-visuals)).
- **Target choice** (`FUN_140437f40`, `FUN_1404374d0`, score `FUN_140435d70`): the preferred/current target is kept if it still scores and equals the order's; else every
  sensed character (list category 4, mask 0x210) scores s = (f·g)² with g = 1 − d/(k × range) (k 1), f 1 (0.1 for slot 0x360, ×0.1 for slot 0x368; slot 0x368 > 0 is 0 for a gunner not of
  the player's faction); dead, `+0x2f8` ≠ 0 or `+0x3d4` (carried) → 0; out of range → 0.001; then × weight (slot 6), × 1.5 when `FUN_1405ca6f0`, + 0.1 when it is the
  current target; × 0.1 when its sense entry is not seen, × 1.5 for the current; best above 1e-6. None: with the player's **Shoot first** option (`DAT_142134690 +0x11a`) a player gunner takes
  sensed characters of fundamental type 0 or 8 or a default-hostile faction (k 0.6); an NPC gunner takes default-hostile ones it is aware of (`FUN_14099b820`) and those fighting a member
  of its own group. An unseen chosen target is looked at (slot 0x3b0).
- **NPC turret goals** 149/222/234 have list A `isNoEnemiesInVicinity` (with the `*` flag): done, so not chosen, while no enemy is near ([ai-tasks](ai-tasks.md#perma-jobs)).
- **Unknown:** the meaning of target slots 0x360/0x368 and `FUN_1405ca6f0`; the facing `+0x458`'s writer; how far "vicinity" is; whether `+0x464` resets when the gunner leaves.

- **Save** (**Observed**): a turret saves its building and machine keys and the `mounted` handle only; the aim error, rotation and reload state are not saved ([save](../formats/save.md#faction-trust-war-flags-bed-rent-and-turrets)).

Meitou ([simulation](../simulation.md#ranged)): `CombatSystem.Turrets.cs`, `TurretGun`, `TurretFormulas`.

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
| max num attack slots | 1 | clamped to 1..5 (CombatClass constructor `FUN_14060a9e0`); enemies that may swing at one target (+ RACE `extra attack slots`, [slots](#attack-slots-and-circling)) |
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

**The ranged-or-melee switch** (**Observed**; no single function decides it). The Ranged order button sets a per-character flag (Character `+0x450` object `+0x129`,
`FUN_1405caa50` id 0x11, from the panel's click `FUN_140721550` through `FUN_1407f3880`). The shooting gate `FUN_1407f2840` needs it, then both arms working, a ranged weapon in the
hands or at the back, and `vfunc 0x368` of the character zero (the player-side test `FUN_1405caa50` also uses). Where the gate holds, the player's attack orders (5, 0x10, 0x3d) become
the shoot orders 0x106 / 0x107 / 0x10a (`FUN_1407f9e20`), i.e. the Shooting tasks 262 / 263 / 266 ([ai-tasks](ai-tasks.md)), whose list A needs `!iAmBeingMeleeAttacked immediate`,
`hasTwoWorkingArm` and `hasWeaponEquipped`; where it does not, attack orders stay melee. The attack-on-sight post and area test `FUN_140598630` skips its 80 / 140 unit melee
reach limits when the gate holds, so a Ranged-on crossbowman engages from afar. Nothing switches back by the target's distance: melee comes from the planner
dropping the Shooting task when a threat is within 40 (`immediate`, [lifecycle](#fight-lifecycle-and-orders), which also has the shooting desire). Meitou: `CombatSystem.ChooseMode` follows that (crossbow unless dry or a threat
is within 40; never with the flag off; NPCs always on). The Shooting rows' desire reads the flag too; the `CharStats` constructor sets it on for every character (the +0x128 dword = 0x100), and only the player's standing order and the save change it, so NPCs are always Ranged. Other unknowns: the reload animation against the timer (the timer paces the shots, so only the clip's look is open); a character's hit shape
(capsule or ragdoll) and whether downed bodies stop bolts; whether the crossbow's bleed and target multipliers and the armour's `ranged skill mult` do anything; how NPC crossbowmen get their bolts.
(The trigger flag `+0x34` is set by the controller tick itself every tick, so a shot leaves in the frame of its decision: [Turrets](#turrets), **Observed**.)

## Unknowns

- The roles of hunger and "fed" in recovery (the `+0x60`/`+0x64` pair is the hunger level and stomach buffer, [character-stats.md](character-stats.md#hunger-and-starvation)). KO wake-up and the KO point: [character-stats.md](character-stats.md#blood-and-ko).
- The steering's force terms (`FUN_1402af1e0`, `FUN_1402e9740`) behind the side-step and the
  min/max gap (the defence pick `140887970` tests no skill, **Observed**). The bluntBase of non-blunt-dominant weapons; the stat that scales max blood; the part `+0x28` link.
- The role of the class-table columns 4-6 (athletics, combat speed, stealth is an inference) and of the slot-adjusted factor (armour item field index 0x5d) in `140898e90`.
- Turrets: see [Turrets](#turrets) for its own unknowns; the meaning of the weight/price-like armour table fields; the remaining difficulty floats beyond the dialog bindings listed above (their consumers).
- What `anim hesitate point` (stored as a frame fraction, no reader found) does; what reads the stumble's `+0x128` = 0.85; where `min cut damage mult` x the manufacturer's `min cut damage` is applied (the engine floors the cut at it); the term shapes of the fist damage (the engine takes half the stat each). The engine's answers are in [simulation.md](../simulation.md#combat).
- Whether the decompiled maths matches in-game numbers: nothing has been compared in play.

## Expression reference

- Combat: `lerp(t, a, b) = a + (b - a)·t`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- Turrets (**Observed** from `GunClassTurret`, `TurretBuilding`, `Task_UseTurret` and the ranged controller; damage loader **Verified**; GUN_DATA and turret BUILDING fields **Verified** by a probe of the install)
- AI (**Observed**)
