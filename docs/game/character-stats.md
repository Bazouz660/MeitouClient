# Character stats and medical state

Kenshi1.0.68 research: CharStats/MedicalSystem binary functions, fcs.def and merged base records.
Evidence scopes, stat IDs, race/part constants and activity tables are retained below.
Implementation: `Meitou.Data.Gameplay.Bodies`, `Meitou.Simulation.Bodies`, FirstAid and Items.
Movement/combat formulas belong to [pathfinding](pathfinding.md) and [combat](combat.md).

## Stats and XP

**Observed:** skills are floats, not discrete levels plus XP counters. Factory combines record
values/randomness, race modifiers and derived stats. Core gain: `f=((cap-stat)/cap)^2`, cap101;
add amount*f only for 0<amount<=20, 0<f<=20, reject new NaN; old NaN becomes20 (exe).
**Verified** in game (1.0.68, scripted harness, issue #3): both limits; the 20 cap is on the
already-scaled amount (event input >148 or general >444 at f=1 adds nothing, not clamped); gain
resumes above101 until f>20 (stat >552 or < -350). A NaN stat stays NaN through
`xpStat_eventBased` and `xpGeneral`: they skip the core's reset (where: **Unknown**). Activity/scaling table defines
continuous/event gains, race factors, skill difference and moving/carrying gains. Global XP3
is stored as0.75; continuous base0.02*dt, event base0.075. Caller clamps are not assumed.

Effective-stat multiplier (`FUN_1406457e0`): H=lerp(h0,1,clamp(hunger-1,0,1)),
L=lerp(l0,1,limb), m=H*L; optional light factor lerp(p0,1,clamp(2.5*P,0,1)); output
clamp(lerp(f0,1,m),0,1), then requested limb-item/gear factors. **Verified** (decompile): P is the
character's light level (Character +0xd8, [perception](perception.md#sight); the multiplier also sets
+0xdc, the resample request), so crafting, science, perception and crossbows suffer in the dark and a
light of 0.4 or more costs nothing. Limb aggregates: A best arm, Hd head, T lowest torso health
fraction. Perception/friendly-fire add the dust storm factor ([gear factors](#gear-factors)). Table
preserves each stat row; fall-through matters. Limb-item product includes0.66 if either arm disabled.
**Verified** in game (#46: `CharStats::getStat` with and without the multiplier, new games, hunger 3 to 1.0,
head 0.75, arms 0.5, torso 0.5): H, L, m=H*L, the output lerp, the aggregates and every row's h0/l0/f0 and
`limb` column except stealth; the stored value never changes. Not tested: p0, the x0.01 and 0.66 arm terms,
gear factors, hunger below 1. The general stat getter `FUN_140884ac0` asks for every term (but sends stealth
[elsewhere](#effective-stealth)); dexterity and strength readers (`FUN_140883a80`, `FUN_140883ba0`, ...) for all
but light and dust. Meitou: `MedicalState.Light` from `SightMap.Light` each tick.

### Effective stealth

**Verified** (decompile; in game "no hunger or arm term", #46): the getter sends stat 16 not to the table but
to `FUN_140884850`: s = clamp(stored x 0.75 if carrying (Character +0x348) x 2 while the posture is not 0
(vfunc 0x368 = `FUN_1405c7e10`, Character +0xe0: crawling or ragdolled, [ragdolls](ragdolls.md#forced-crawl)) x encumbrance (CharStats +0x190) x the body term (medical +0xb4) x the gear's stealth
(CharStats +0x3c) x the fitted limbs' factor `FUN_140644b90(16)` x 0.01, 0, 1); the getter returns s x 100 and
the sneak speed `FUN_140884920` reads s. Body term (`FUN_140644da0`): clamp(lowest health fraction over TORSO and
LEG parts, 1 with none, + toughness x scale x 0.01, 0, 1). Its only caller is the sense update `FUN_1408586d0`,
past an early return on a Character +0xd4 byte, so otherwise it keeps its last value (1 at start; **Observed**,
fits #46's torso at 0.5 leaving stealth at 1). The table's stealth case (A; 0.9, 0.9, 0.8) exists, but no caller
found reaches it. Meitou: `Orders.Stealth`; its x2 is for the forced crawl only (Meitou has no voluntary crawl).

### Gear factors

**Verified** (decompile of `FUN_140887030`, the worn-gear refresh, its call to `FUN_140884370`, and the ARMOUR
loader `FUN_140898e90`). Over the worn armour list (Character +0x4a8/+0x4b0) it multiplies the pieces' floats
into CharStats: +0x40 `asassination mult` (assassination row), +0x44 `dexterity mult` (dexterity row), +0x48
`damage output mult` (katana, sabres, hackers, heavy, blunt rows; used only when not 1 and above 0), +0x50
`ranged skill mult` (crossbows and friendly fire rows), +0x38 `dodge mult`, +0x3c stealth, +0x4c `fist injury
mult`; sums `perception bonus` (+0x30), `unarmed bonus` (+0x2c) and the attack/defence bonuses. Stealth and dodge
both start from the worn backpack's `stealth mult` (CONTAINER +0x248, `FUN_140763810`; negative becomes 1), 1
without one. With no armour and no backpack (inventory section 12) dodge and dexterity are 1.1 and stealth 1.2
(literals at `14169bb44`, `1416852f0`). A piece's stealth (ARMOUR +0x25c, disassembly of `FUN_140898e90`), with
t = quality + `level bonus` x 0.01: lerp(t, material lo, hi) x lerp(t, class lo, hi) x `stealth mult`; 1 if
`stealth mult` is negative; a hat (`slot` 3) that lists coverage is raised to 1. The tables (constructor
`FUN_1408a8f90`, material +0x1c, class +0x18 of the [economy](economy.md#42-armour-and-clothing-type-3) tables):
CLOTH and LEATHER 1..1, CHAIN 0.3..0.7, METAL_PLATE 0.1..0.8; GEAR_CLOTH and LIGHT 1..1, MEDIUM 0.9..0.9,
HEAVY 0..0.4. The stun row factor `FUN_1406439f0`
is the dust storm: lerp(1, 0.5, v) while medical +0x148 = 1, v = medical +0x14c = the storm strength x (1 -
the gear's protection), clamped 0..1 (`FUN_140648ef0`, which also posts "You are in a dust storm, it affects
your accuracy in combat."). Meitou: `Fighter.Factors` / `Naked` / `StealthMult`, `ArmourPiece.Stealth` (the dust value is `MedicalState.DustStatFactor`,
[weather](../formats/weather.md#effects-on-characters)); crossbow and assassination skills take their factor in `CombatSystem.Eff`,
dexterity and damage output stay applied by the combat formulas (`DexterityMult`, `DamageOutput`, the latter
to the whole packet where the original scales the weapon skill: **Observed** difference, not changed).

## Hunger, blood and parts

**Verified** hunger thresholds: starts3, >=2 unpenalized, <2 penalized, <1 starving, <=0 death.
**Observed** tick: T=starvation-time*HungerTime (base60 hours per level). Empty stomach drains
dt/T*rate; fed stomach converts buffer into hunger, capped3. Rate includes race, rest/KO/dead
bed factor0.33, active encumbrance factor and situation terms. Zero hunger-rate races do not
starve; robot flag alone does not imply zero rate. Eating: [Eating](#eating); NPCs' free food:
[Free food](#free-food). Starvation KO threshold uses toughness10..85 and doubles with food;
actual transition timing is not fully decoded.

Medical parts retain flesh h, bandage b, stun s, wear w, maximum M and hit/rig/replacement state.
Effective part health, coverage/hit choice and body aggregates are distinct. Apply blunt/cut,
bandaging and stun to the correct quantities; do not heal untreated flesh directly.

### Time

**Verified** (decompile): the character adds `DAT_142133794` (real seconds x game speed, [game-loop](game-loop.md#the-three-time-values))
to Character +0xc8 every frame (`FUN_1405c7ae0`, `FUN_1405cfd90`); `FUN_1405c7d40` hands the sum to the medical update
`FUN_140652000` and clears it. So part, blood, clot and KO-timer rates are per second of that time (a game hour is 1200/11 of them).
Hunger instead reads the game clock's elapsed time (`FUN_14064da70` via `FUN_140791ca0`). Meitou: `MedicalState.Tick(TickSeconds)`.

### Part tick (`FUN_14064f5c0`, **Verified** decompile)

In order, per part: the hit multiplier falls 0.01 a second while h >= 0 (reset to 1 below). Self healing (RACE, conscious, alive,
h - s < M - b) adds 0.05 dt `heal rate mult` R 0.4 to b and skips degeneration that tick; otherwise, while h < M and h > -3M,
h -= 0.05 dt `bodypart degeneration rate` ChanceOfDeath degMult D 1.2 with D = clamp(((M-h-b)100/M - 20)/70, 0, 1), quartered
when h + b > 0; degMult lerps 1.7..0.03 by toughness and is 0 for a non-vital robotic part (robot race or limb replacement)
and in any bed. Healing: while h < M, b > 0 and the limb is not crushed (state 3), min(b, 0.05 dt `heal rate mult` R), x250
on a robotic part, moves from b to h. R = M/100 x the organic rate (robotic for a robot race or a replacement); a stump
(state 1) has base HP 5 and R = 2 x the organic rate without M/100. Repair (a repair bed) moves 0.05 dt `resting heal rate
mult` x its rate from w into b. Rig is held under 50 - (h - s); s falls 0.05 dt `stun recovery rate` `heal rate mult` R.

### Beds (`FUN_140652000`, **Verified** decompile)

Character state 1 (in something): degeneration 0 for every part. A BF_BED (6) scales the organic rate by the Building's
+0x3c0 = `output rate` x the functionality's `production mult` x the research production multiplier (`FUN_140297b30`; base
data: Bed 8, Camp Bed 4, Fish Bed 2, Old Bed 1); a BF_SKELETON_BED (25) the robotic rate and the repair rate by that value
times a slot 0x368 of its slot-0x300 object (**Unknown**, taken as 1). Blood recovers x `resting heal rate mult` only in
state 1; hunger takes `bed hunger rate` in state 1 or KO or dead. State 2 (held in a cage) doubles the organic rate; a
third x2 (both rates) needs a character slot 0x248 that returns 0 for `Character` and `CharacterHuman` (**Unknown** which class).
Meitou: `BodySystem.BedOf`, the bed fixture a character is held in (`BedSystem`) or an AI sleeps at.

### Blood and KO

**Verified** (decompile): wounds lose `bleeding clot rate` x0.1 a second (`FUN_140646140`); a hit opens one of strength
max(cut, pierce) x `bleed rate` x0.1 x scale (`FUN_1406508d0`). Bleeding = (`extra blood loss from bodyparts` x stumps
(-(h+b) 0.002 each) + wounds) x ChanceOfDeath x 1.2 x race `bleed rate`; blood falls by it a second and, with no bleeding,
recovers `blood recovery rate` x0.1 x race `heal rate` a second. The KO timer +0xa0 falls by dt.

**Verified** coma point=-lerp(10,85,toughness/100). **Observed** KO duration combines base20,
vital/negative-part and missing-blood terms; leg loss adds a separate random KO, arm loss does
not. Death: vital h-s<-M, blood<=-capacity or hunger<=0. Preserve KO/coma/death distinctions.

## Aid, limbs and load

First aid fills bandage; healing consumes it. Medkits, robot repair and beds apply separate
skills/charges/rates. Limb replacements modify part health and stat factors; missing limbs,
stumps and robot wear are not interchangeable. Race tables own HP/blood/hunger/heal/XP inputs.

**Observed** (`wade`, player Metaru's MEDICAL_STATE): injured parts have flesh + bandage ≈100,
stun0 and bleeding0; the wounds are already treated. First Aid has nothing left to bandage;
the HUD's yellow layer shows the treated damage awaiting passive recovery.

**Observed** (play, 2026-10-21): a first-aid order (self included) given to a character without a medkit is refused before
any movement or animation, with the bubble "I don't have a medkit." Whether a squad mate's kit nearby or a robot repair kit
avoids the refusal is **Unknown** (Meitou accepts any kit `FindKit` would use).

**Verified** carried person adds CONSTANTS weight30, independent of body/race/inventory.
**Verified** in game (1.0.65, scripted harness, issue #45; strength -20..45, W 15/16.4/46.4 kg, torso wounds, hunger):
capacity C=1.2*s+15, unrounded, s = strength through its whole stat multiplier (hunger too, floor 0.1); encumbrance
clamp((C-0.05W)/(0.95W),0,1), both clamps seen, W includes a carried person. Stored at CharStats+0x190, the return of
`FUN_1408838b0`. **Observed**: it affects speed, hunger, XP/dodge (consumers not checked in game).
Weapon inventory weight multiplier0.5; backpack effect scales contents.
**Verified** ITEM `stackable` is integer capacity,1 means one per cell; bolt items use charges
for shots. Gear/container/map/crossbow/limb item types cap1. Other stacks use
trunc(max(base,section-min)*section-mult); a section flag forces base1 (flag scope unknown).

### Splints

**Verified** (disassembly of `FUN_140646400`, `FUN_140649d50`, `FUN_1405988e0`; Splint Kit records in gamedata.base). A splint kit is an
ITEM with `item function` 2 (Splint Kit q20, rebirth.mod q25; Advanced Splint Kit q50; 100 charges). A splint raises a part's rig, which
counts as health in the fraction, the down (h-s+rig<0) and sever tests, so a splinted broken leg walks again (leg speed reads the fraction).
- `needsSplint`(X): patient not dead, race not `is robot`, some non-vital part with rig<b, (h-s+rig)*1.05<50 and rig+1<trunc(X). The
  menu asks with X=50 (verb "Splint Injuries", task 249 "Splinting"), a medic with min(medic, kit quality). Robotic limbs are not excluded.
- Update: t=min(medic skill, kit quality)*0.01, cap=1-trunc(t*-100), t in float and the product rounded to float, so quality 20 caps at 20 (**Verified**, `MULSS`/`CVTSD2SS` at 0x140649ddb..e1b). The first non-vital part in part order with h-s+rig<50, rig<s+b,
  rig<cap gets rig+=lerp(1,15,t)*medic speed mult*dt, clamped to s+b and cap; so only bandaged or stunned damage can be splinted.
  The kit drains like a medkit only when rig was added; medic XP (stat 9) each update. Ends when no part qualifies.
- No time decay and hits do not touch rig. **Observed** (`FUN_14064f5c0`): once h-s+rig>50, rig=max(0, 50-(h-s)).
- Order 249 refuses without a kit ("I don't have a splint kit."). The job 250 "Splint Rigging" (finder `FUN_14099e240`, may pick the medic
  itself): [perma-jobs](ai-tasks.md#perma-jobs); Meitou runs it as a splint treatment (`FirstAidSystem.SplintSomeone`).
- Meitou: `SplintOrder` (the Treat task with `TreatSplint`), `MedicalState.NeedsSplint` / `Splint`; dt in game seconds as for first aid.

### The stat list

**Verified.** Three independent places agree on the numbering: the editor's `StatsEnumerated` enum (names), the
per-stat accessor `FUN_140884190` (stat number to `CharStats` byte offset, a switch), and the stats save writer
`FUN_14064a8f0` (offset to saved field name; read back by the loader `FUN_14064b6a0`, which stores at the same
offsets). Stats are `float`s inside `CharStats`. The number is what RACE `heal stat`, `stats good<N>`,
`stats bad<N>`, BUILDING `stat used` and all XP code pass around.

| # | `StatsEnumerated` | STATS field / save name | `CharStats` offset |
| --- | --- | --- | --- |
| 1 | STAT_STRENGTH | `strength` | +0x80 |
| 2 | STAT_MELEE_ATTACK | `attack` | +0x120 |
| 3 | STAT_LABOURING | `labouring` | +0xe4 |
| 4 | STAT_SCIENCE | `science` | +0xe0 |
| 5 | STAT_ENGINEERING | `engineer` | +0xcc |
| 6 | STAT_ROBOTICS | `robotics` | +0xdc |
| 7 | STAT_SMITHING_WEAPON | `weapon smith` | +0xd0 |
| 8 | STAT_SMITHING_ARMOUR | `armour smith` | +0xd4 |
| 9 | STAT_MEDIC | `medic` | +0x98 |
| 10 | STAT_THIEVING | `thievery` | +0xac |
| 11 | STAT_TURRETS | `turrets` | +0x114 |
| 12 | STAT_FARMING | `farming` | +0xe8 |
| 13 | STAT_COOKING | `cooking` | +0xec |
| 14 | STAT_HIVEMEDIC | shares `medic` | +0x98 |
| 15 | STAT_VET | shares `medic` | +0x98 |
| 16 | STAT_STEALTH | `stealth` | +0xa4 |
| 17 | STAT_ATHLETICS | `athletics` | +0x94 |
| 18 | STAT_DEXTERITY | `dexterity` | +0x88 |
| 19 | STAT_MELEE_DEFENCE | `defence` | +0x124 |
| 20 | STAT_WEAPONS | no storage (the accessor returns a dummy slot) | n/a |
| 21 | STAT_TOUGHNESS | `toughness2` | +0x90 |
| 22 | STAT_ASSASSINATION | `assassin` | +0xb8 |
| 23 | STAT_SWIMMING | `swimming` | +0xa8 |
| 24 | STAT_PERCEPTION | `perception` | +0x8c |
| 25 | STAT_KATANAS | `katana` | +0xf8 |
| 26 | STAT_SABRES | `sabres` | +0xfc |
| 27 | STAT_HACKERS | `hackers` | +0x100 |
| 28 | STAT_HEAVYWEAPONS | `heavy weapons` | +0x108 |
| 29 | STAT_BLUNT | `blunt` | +0x104 |
| 30 | STAT_MARTIALARTS | `unarmed` | +0x10c |
| 31 | STAT_MASSCOMBAT | `mass combat` (save only) | +0x9c |
| 32 | STAT_DODGE | `dodge` | +0xf0 |
| 33 | STAT_SURVIVAL | no storage (dummy) | n/a |
| 34 | STAT_POLEARMS | `poles` | +0x118 |
| 35 | STAT_CROSSBOWS | `bow` | +0x110 |
| 36 | STAT_FRIENDLY_FIRE | `ff` | +0xf4 |
| 37 | STAT_LOCKPICKING | `lockpicking` | +0xb0 |
| 38 | STAT_SMITHING_BOW | `bow smith` | +0xd8 |

### Levelling (XP)

| Activity | Function | Effect (**Observed**) |
| --- | --- | --- |
| Hit landed (events 0 and 1) | `FUN_1408c6980` | `attack` (or `unarmed` when the stat-set kind at +0x1f0 is 5) gets 0.075 x skill-diff(vs opponent defence) x `map[2]`; the current weapon skill, `dexterity` (x `map[18]`) similarly; event 1 uses halves (0.5 and 0.4) |
| Attack defended (event 2) | same | `defence` at 0.25 of base, weapon skill at 0.1 |
| Being hit (event 4) | same | `defence` 2.0 of base (**Verified**: the stat written is the CharStats slot +0x124, the same slot event 2 uses for `defence`, and [combat.md](character-stats.md#stats-and-xp) lists the same; `FUN_1408c6360` handles stat-set kind 5), and `toughness` = 0.075 x `xp rate toughness` (1.33) x skill-diff(own toughness vs damage) x `map[21]` |
| Event 6 | same | the same attacker-side gains as event 2 (attack/unarmed 0.25, weapon skill 0.1) but no `defence` gain (meaning of event 6 not decoded) |
| Strength tail of every event | same | `strength` rises by `core(0.075 * 0.55 * 2*xp rate strength * map[1])` scaled x0.4 for event 2, x0.1 for event 4, and by the weapon-weight factor below; for unarmed (kind 5) the weapon-weight factor is replaced by (1 - encumbrance factor) |
| Toughness per damage received | `FUN_1408c68e0`, called from `FUN_140666780` | 0.075 x k x `xp rate toughness` x `map[21]`; k = 0.05 normally, 0.25 for damage kind 1, 0.5 for kind 5 (kind meanings not decoded) |
| Toughness when knocked out | `FUN_1408c6720` (from the part damage `FUN_14064f300` when a hit knocks out, unless its fourth argument is set) | **Verified**: core(0.075 x `map[21]`) (`DAT_142134edc` = exp gain multiplier x 0.1, set in `FUN_1408c6980`) |
| Toughness while down (task `FUN_14033ca10`) | `FUN_1408c6780` | **Verified**: core(f x (n + 10) x 0.075 x `map[21]`), f = 1 (+1 left arm, +2 right arm, +2 blood KO, +2 medical +0xa4 < 15), n an int of the slot-0x68 object (**Unknown**); dialogue events 0x17 to 0x19. Not in Meitou |
| Toughness from losing a limb | `FUN_14064edc0` via `FUN_1408c7310` | generic entry with dt 1, amount 300 into stat 21 (a large one-off gain) |
| Weapon-weight strength factor | `FUN_1408838e0` / `FUN_1408839a0` | `min strength xp mult` (0.1) + clamp((weapon weight - strength x injury multiplier) / `weight strength diff 1x` (20), 0, `weight strength diff max` (1)); stored at +0x184 |
| Strength from carrying | `FUN_1408c60c0` (from the movement tick `FUN_1408c6660`) | (1 - encumbrance factor)^2 capped at 0.5, x2 unless in state 0xb, x `xp rate strength from walking` (0.5) |
| Athletics (running) | `FUN_1408c6660` | `FUN_1408c5ec0` (the encumbrance factor, x1.5 when the speed is over 80% of the character's maximum) x `xp rate athletics` (stored 0.5) via the generic entry; swimming (stat 23) gets 5, or 10 when carrying, while in water |
| Stealth (sneaking) | `FUN_1408c65a0` (from the sense update `FUN_1408586d0`) | continuous entry into stat 16 with amount 0.2; 1.5 when observed (enemies about, Observed) and a watcher is noticing or detecting, 0.9 when observed but none is; x0.1 when not sneaking (called then only in some other states); 0 when KO'd or in a non-default state. Resets the exposure factor +0x174 to 1 (**Verified** literals) |
| Stealth (heard check) | `FUN_1408c7330` (from hearing `FUN_140857500`) | generic entry, dt 1, amount (2 if flag a + 2 if flag b) x20 if flag c, times the exposure factor, which then x0.7; flags **Unknown**. Not modelled |
| Lockpicking | `FUN_1408c62e0` | stat 37 gains 1.2 x `map[37]` on success, 0.24 x on failure |
| Engineering | `FUN_1408c6250` | stat 5 gains `0.02 * dt * 0.75 * map[5] * 0.75` |
| Medic | `FUN_1408c6130` | **Verified**: `dt / T * 0.75 * map[patient race's heal stat]` added straight to the stat (no core curve, no cap); T = lerp(`XP rate medic 1`, `XP rate medic 99`, skill x 0.01) (the ends' order from the field names; editor text "seconds of usage per point"); nothing when T <= 0 |
| Mass combat | `FUN_1408c5e80` | **Verified**: +0x9c rises by 0.75 x 0.05 per call, straight; one caller, the answer to a slot ask `FUN_140666a30` ([combat](combat.md#attack-slots-and-circling): the asked character's, while stumbling or fighting; nothing reads the stat; not called in Meitou) |
| Other crafting and machine skills | callers of `FUN_1408c7250` / `FUN_1408c7310` | `FUN_1408c7250(stat)` picks a per-stat base `amount` and calls the generic entry with a global float as `dt` (`DAT_142133794`, the game-speed-scaled frame time, the third time value of [game-loop.md](game-loop.md#the-three-time-values)): 0.5 by default, thievery and lockpicking 0.1, turrets 1.0, perception 3.0, farming 0.75, crossbows 1.2 (**Observed**; the farming and crossbows values come from a switch fall-through, 0.5 x 1.5 and 0.8 x 1.5). `FUN_1408c7310(stat, amount)` is the same with dt 1 |

### Effective stats: injuries and hunger

Decoded cases (hex constants converted). `limb`, h0, l0 and f0 are **Verified** in game for every row (#46,
[above](#stats-and-xp)); p0, the arm-disabled terms and `extra` stay **Observed** (hand-decoded from a switch with
fall-through cases):

| Stats | `limb` | h0 | l0 | f0 | p0 | extra |
| --- | --- | --- | --- | --- | --- | --- |
| strength (1) | A x T | 0 | 0.75 | 0.1 | none | |
| melee attack (2), unarmed (30) | A x T | 0.75 | 0 | 0.7 | none | |
| labouring, engineer, weapon smith, armour smith, farming, bow smith (3,5,7,8,12,38) | A x T, x0.01 if both arms disabled | 0.5 | 0 | 0.25 | 0.25 | |
| science, robotics, cooking (4,6,13) | Hd (falls through into the medic row) | 0.6 | 0.4 | 0.25 | 0.25 | |
| medic, turrets, hive medic, vet (9,11,14,15) | Hd | 0.6 | 0.4 | 0.25 | none | |
| thievery, lockpicking (10,37) | A, x0.01 if both arms disabled | 0.75 | 0.4 | 0.25 | none | |
| stealth (16) | A (unreached: [effective stealth](#effective-stealth)) | 0.9 | 0.9 | 0.8 | none | |
| athletics (17) | 1 | 0.7 | 1 | 0.5 | none | |
| dexterity (18) | A x T | 0.7 | 0 | 0.4 | none | x CharStats+0x44 if requested |
| defence (19) | A x Hd | 0.75 | 0 | 0.8 | none | |
| assassin (22) | 1, or A x0.01 if both arms disabled | 1 | 0 | 0.5 | none | x CharStats+0x40 |
| swimming (23) | A x T | 0.5 | 0 | 0.01 | none | |
| perception (24) | Hd | 0.6 | 0.5 | 0.33 | 0.25 | x `FUN_1406439f0` |
| crossbows (35) | Hd | 0.6 | 0.4 | 0.25 | 0.5 | x CharStats+0x50 |
| friendly fire (36) | Hd (falls through into the perception row) | 0.6 | 0.5 | 0.33 | 0.25 | x CharStats+0x50, x `FUN_1406439f0` |
| katana, sabres, hackers, heavy, blunt (25 to 29) | | | | | | x CharStats+0x48 only |
| all others (toughness, ...) | | | | | | 1.0 |

### Hunger and starvation

The medical object holds a **hunger level** (`+0x60`, 0 to 3, the UI shows level x 100 as "Hunger") and a
**stomach buffer** (`+0x64`, food waiting to be digested). A new character starts at 3.0 (constructor
`FUN_1406409f0` and `FUN_1406464c0`). **Verified** thresholds (the comparisons against 2.0 and 1.0 in the status
panel `FUN_14088a8f0`, the hunger term of the stat multiplier `FUN_1406457e0` and the speed factor
`FUN_140644d30`, and `../animation.md`'s starvation shape reading the same float):

| Hunger level | State |
| --- | --- |
| 2 or above | well fed, no problem |
| below 2, at or above 1 | stat penalty and up to half movement speed; the panel says "Hungry" when the stomach buffer is empty and "Malnourished" ("fed and slowly recovering") when there is food in it (**Observed**; the panel also needs a value at medical +0xa4 of at least 0.7, meaning not traced) |
| below 1 | "Starving": the character passes out below a toughness-dependent point; dies at 0 |

### Eating

**Verified** (decompile of `FUN_1405d29c0`, `FUN_1405d0780`, `FUN_140748d70`, `FUN_1405c7f70`, `FUN_1405c9d90`,
`FUN_14042ee50`). The character update (`FUN_1405dc650`, vtable +0x2b0) runs the auto-eat each update; there is no
eat task, animation or player command (a right click on food does nothing, [ui-screens](ui-screens.md)).
- **Wants food** (slot 0x340): a character of the player's faction or a slave (status +0x1a0 = 1) always,
  anyone else below hunger 2.1. **Hungry**: wants food, stomach (+0x64) empty, hunger < 2.5.
- **Order**: an animal (slot 0x248) eats nothing while the squad option Feed Animals (+0x114) is off. Hungry:
  own inventory, then the worn backpack's. Else, for a character of the player's side (`FUN_140856bc0`) with
  an empty stomach, hunger < 2.25 and Share Food (+0x115) on: the backpack of each squad member (squad order)
  within 200 units. Knocked-out characters eat too (the status text "You have passed out from starvation. You
  have food though...").
- **Choice** (`FUN_140748d70`): first `item function` 15 ("Possibly Edible": raw meat) the race eats (RACE
  `special food` lists it, or any for an animal), then 3 (food). Score: opened (charges +0x118 below full
  +0x114) (2 - charges/full) x 1000, else full / 100; the highest wins. Death items of a living owner skipped.
- **Bite** (`FUN_1405d0780`): n = min(charges, 300 - hunger, 50) (a limb, function 17: 30, eaten whole); the item
  loses n and goes at 0; stomach = n / 100; "Ate {1}" floats 15 up (white, alpha 0.9); Wwise switch
  Consume=Eat and `Character_Consume` within 200 units of the camera. The stomach then digests (hunger tick).
- Food records do not stack (`stackable` 1 on all 15 base food items, **Verified** by probe). `vampiric`
  (spiders, land bats, skimmers) is read only by the loader here; it concerns eating downed bodies.
- AI slot 25 (`FUN_140597d30`, the AI's eat desire) is 1 when hungry (hunger <= 2.4), AI +0xb0 and +0xb4 not
  above 0 and no food in the inventory; 0.1 when +0xb4 > 0 and hunger < 1.2. Which goal reads it is **Unknown**.
- **Mouth** (**Verified**, decompile of `FUN_14034f340`, `FUN_1405d0b50`, `FUN_1405c8240`, `FUN_1405d0d20`, `FUN_1405dc650`; the attach point is the string `mouth`,
  `FUN_141534950`). Only a **limb** (item function 17, item `+0x124`) picked up by an animal (vfunc 0x248 non-null) goes to the mouth; any other item, and a limb picked up
  by a person, goes into the inventory (the pickup task, below). Taking it: the limb held before is let go (`FUN_1405c8240`), the new one is flagged held (`+0xd8` = 1), attached to the
  `mouth` point and the time is stamped (`+0x6e0`). With no `mouth` attach point on the skeleton the limb is bitten on the spot instead (the same function's other branch).
  While the mouth is full, the character update (`FUN_1405dc650`) does nothing else about food (no auto-eat, no Feed Animals check), and t = game minutes since the stamp, f = slot 0x398
  (the age, [animals](animals.md#ageing)): an animal with f > 0.9 of the player's faction (`FACTION +0x250`) eats when the stomach (+0x4bc) is empty and hunger (+0x4b8) below 2.6, or at t > 15;
  any other eats when the stomach is empty and t > 45, or at t > 60 + 120 x (1 - f). Eating clears the mouth, the flag, and bites the limb (whole, 30).
  Which orders and goals send an animal to a ground limb is **Unknown** (the data's animal goals "eating food on the ground" are typed IDLE, [animals](animals.md#ai-goals)); Meitou reaches the mouth only through the player's pickup order.
  Meitou (`FeedSystem.HoldInMouth`, `MouthDue`; `CharacterCold.Mouth`): as above. Engine choices: every animal has the `mouth` point; a held
  limb is neither saved nor carried across a load; it goes with the animal if the animal dies or is removed.
- Meitou: `FeedSystem` (checks every 30 ticks, engine choice), squad options `SquadAi.Feed` / `Share` (CAMERA
  bools `feed`, `share`, **Verified** reader `FUN_1403ebf50`; default on, **Unknown**).

### Free food

**Verified** (decompile of the hunger tick `FUN_14064da70` and slot 0x348 `FUN_1405c9ca0`). Each hunger tick,
before the drain: if the character's floor (slot 0x348) is above its hunger and its stomach is empty, the
stomach gets min(floor - hunger, 0.04). Nobody but the player has to feed anyone; NPCs hover at their floor.

| Character | Floor |
| --- | --- |
| animal (slot 0x248), unless the byte `142133599` (next to "Bandits loot the player", no UI binding; **Unknown**) is set | 3 |
| slave (status 1) | 1 |
| imprisoned (+0x2f8 = 2): held by the player's faction / free / escaped slave | -10 / 2 / 1.15 |
| player's faction | -10 (never) |
| anyone else | its platoon's +0x110: 3, or for a SQUAD_TEMPLATE with `malnourished` 1 + 1.2 x rand (`FUN_1407ee2d0`) |

Drain and digestion (same function): stomach empty: hunger -= dt/T x race rate x situation (`FUN_1408837a0`:
bed/KO/dead `bed hunger rate`; else in combat or moving (movement +0x24) 1 + (1 - e) `encumbrance hunger
rate`; else using a machine 0.8; else CharStats +0x178, writer **Unknown**); with food: moved = min(stomach,
dt/T x slot 0x390 x `fed recovery rate mult` x race rate). Hunger is capped at 3 before the update. The tick
is skipped while `DAT_142134690` +0x2f0 is set (**Unknown** flag). Meitou: `MedicalContext.HungerFloor`
(`BodySystem.HungerFloor`; no floor in a world without data); Meitou keeps the factor 1 + (1 - e) rate when idle.

### Corpses

**Verified** (decompile of `CharacterHuman::vfunc_29` `FUN_1405ccd90`, game clock `FUN_14066cc00` in hours). The
death time is set on the first update as a corpse. A corpse neither carried (+0x3d4) nor in a bed or cage
(+0x2f8 = 0) is destroyed ("corpse decayed") once more than 12 hours have passed; while held, the death time is
kept at least now - 6, so it lies 6 more hours once let go. A corpse whose zone is loaded but not active is
destroyed at once ("corpse unloaded"), carried or not: the zone is the 64 x 64 grid cell of the corpse's x/z (`FUN_140a08b20`, `FUN_140a08ae0`: ZoneMap record 0x168 bytes from `+200`
of the zone manager) and "active" is its byte `+0xb1` (`FUN_140a090b0`; `+0xb0` is a second flag, `FUN_140a090d0`). A position outside the grid is never removed. What sets `+0xb1` was not
traced; it is read as the zone being active round a player character (**Observed**: the callers use it for "zone is live", e.g. the squad-leader pick `FUN_140792b90`).
Meitou: `BodySystem.SlowWorld`; "active" is `World.ActiveZones`, the population's active set (the box of +-340 round each player character, [game-loop](game-loop.md)), unknown before the
population's first look (then nothing is removed). Engine choice: the original's active set may differ from that box.

### What a body part is

A character's parts come from its RACE `combat anatomy` reference list (**Verified** reader `FUN_14064dd10`; every one of the 53 base-load races lists 7 LOCATIONAL_DAMAGE references,
except the No-Head MkII skeleton with 6; there are 9 LOCATIONAL_DAMAGE records in all, the two extra ones being
the forelegs). Each reference carries two integers. **Observed**: value 1 is the part's **base
HP** (100 for humans, 250 Garru, 75 hive workers, 2500 to 5000 Leviathan) and value 0 its **hit weight** (a
relative chance to be struck; the hit resolver `FUN_1406508d0` builds a cumulative-weight map from it and draws a
random number). The assignment of the two integers to the two constructor arguments is inferred, not visible in
the decompile. Base-game anatomies (value 0 / value 1, probe over RACE `combat anatomy`):


LOCATIONAL_DAMAGE records (**Verified**: all 9 base records by probe; loader `FUN_14064d0e0`; Head, Chest,
Stomach, two arms, two legs, two forelegs):

| Record | type | collapses | death (vital) | severance | collapse part | affects move speed | affects skills | KO mult |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Head | 3 HEAD | yes | yes | yes | 1 | 0 | 0.6 | 2 |
| Chest | 0 TORSO | yes | yes | no | 1 | 0 | 0 | 1 |
| Stomach | 0 TORSO | yes | yes | yes | 1 | 0.3 | 0 | 1 |
| Left Arm | 2 ARM | yes | no | yes | 4 | 0 | 0.25 | 0.5 |
| Right Arm | 2 ARM | yes | no | yes | 2 | 0 | 1 | 0.5 |
| Left / Right Leg | 1 LEG | yes | no | yes | 32 / 16 | 0 | 0 | 0.5 |
| Left / Right Foreleg | 2 ARM | no | no | yes | 4 / 2 | 0 | 0.25 / 1 | 0.5 |

#### Part order

**Verified** (decompilation, `MedicalSystem` setup `FUN_14064e410`): the parts are gathered from a map keyed by their LOCATIONAL_DAMAGE record, each with a
byte key = table[`body part type`] (`0x1416fc42c`: torso 10, leg 30, arm 20, head 0) + 1 when `collapse part` is 2 or 0x10 (right), sorted ascending
(`FUN_14065d8e0`) into the part array. That array is the medical panel's row order, `parts[0]` the editor's "Hit Points", and the order saves write: head, torso,
left then right arm (forelegs too), left then right leg. Parts with one key (chest, stomach) keep the map's order, which is keyed by pointer (**Unknown**;
vanilla shows stomach first). Meitou: `MedicalState.Create` and `ReadSave` sort the parts by `BodyPartTemplate.MedicalOrder`, ties in `combat anatomy` (or saved) order, so
the stomach comes first; the panel, splinting's "first part", the hit table and saves all use that list.

### Per-part state (`MedicalSystem::HealthPartStatus`)

**Observed**, from `FUN_14064d0e0` (constructor), `FUN_14064f5c0` (update), `FUN_14064f300` / `FUN_140644a70`
(damage), `FUN_140644e60` (aggregates).

| Offset | Meaning |
| --- | --- |
| +0x00 | LOCATIONAL_DAMAGE record |
| +0x08 | body part type 0 to 3 |
| +0x10 / +0x18 | medical system / character |
| +0x20 | side (0 none, 1 left, 2 right) |
| +0x28 | fitted replacement limb (non-null: robotic) |
| +0x30 / +0x31 / +0x32 | `self healing` (RACE) / `collapses` / vital (`death`) |
| +0x34 | `KO mult` |
| +0x38 / +0x3c | hit weight (ref value 0) / transient hit-focus multiplier (starts 1) |
| +0x40 | flesh HP **h** (may go negative, down to -3 M) |
| +0x44 | **stun** s (blunt damage lands here) |
| +0x48 | **bandaged pool** b: damage that has been treated and is waiting to be healed |
| +0x4c | **rig** (save key `rig`): the splint, added to h - s by the fraction, the collapse and the sever tests ([Splints](#splints)) |
| +0x50 | **wear** w (permanent loss of maximum, robots) |
| +0x54 / +0x58 / +0x5c | base HP (ref value 1; forced to 5 for a stump) / multiplier (set by the constructor and rewritten each tick from the character slot-0x390 factor) / body scale (character slot 0x238) |
| +0x60 | health fraction `((h - s) + extra) / M` |

### Limb replacements and robots

A LIMB_REPLACEMENT record (`slot` 50 left arm, 51 right arm, 52 left leg, 53 right leg; **Verified**) becomes a
`RobotLimbItem` with a `StatsEnumerated -> float` multiplier map. Each value is stored as two fields, `X` at the
lowest quality and `X 1` at the highest, scaled by the item's `level` (Gear +0x224 int, +0x220 = level x 0.01), not
the save's `quality` float. Constructor `read_athletics_mult_1_n17` (`0x1400ce2d0`; **Verified**, disassembly and
constant bytes):
- `HP` (+0x268), `unarmed damage bonus` (+0x278, truncated to int) and `swimming mult` (stat 23, every slot):
  lerp(level x 0.01, X, X 1), unclamped. Where +0x278 is read is **Unknown** (Meitou: not wired).
- Skill multipliers (arms 50/51: strength, dexterity, thievery for 10 and 37, ranged; legs 52/53: athletics,
  stealth) via `FUN_1400cd500(level, X, X 1)`: if X < 1 <= X 1, level >= 40 gives lerp(clamp((level-40)/55,0,1), 1, X 1)
  and level < 40 gives lerp(clamp((level-5)/35,0,1), X, 1), so the grade bands 5/40/95 hit X, 1, X 1; otherwise
  lerp(level x 0.01, X, X 1).
- `overall mult` is looked up and discarded; any other use is **Unknown**.

**Observed**: METARU's Scout Legs (`level` 0, `quality` 95) give legs of 50 HP, the low end of 50/150. Crafted limbs
take the bench's grade roll ([buildings-production](buildings-production.md)), shop limbs the template's `robotics levels`
spread ([economy](economy.md) 9.1).

Per limb slot k (0 L arm, 1 R arm, 2 L leg, 3 R leg) the character holds a state (0 original, 1 stump, 2
replaced, 3 crushed) and the fitted item (holder `FUN_1400cd3f0`; **Verified** by the save code below).
- Fit (`FUN_1406445b0`): part base HP = item HP, wear = item `wear`, stun = item `stun`, flesh =
  min(base - wear - item `dam`, base - wear). Remove: the item takes `dam` = base - wear - flesh, `wear`,
  `stun`; the part becomes base 5, flesh 5, wear/stun 0. On load the item is fitted keeping the saved part floats.
- Sever (`FUN_14064edc0`) returns for a replaced slot; from state 0 or 3 it makes a stump, awards toughness,
  drops the RACE `severed limbs` item of the slot at `bone name 2` and opens a wound.
- CHARACTER `starting health` (LOCATIONAL_DAMAGE, val0), applied to a new (not loaded) character (`FUN_140645ef0`, **Observed**): with
  M the part's maximum, val0 < -M makes the limb a stump (flesh 0, bandaged pool 5); else flesh = clamp(val0, -M, M) and the
  bandaged pool M - flesh. **Verified** by probe: Rock Bottom's `Naked dude` has Left Arm -150, a left-arm stump. Meitou ignores it on non-limbs.
- A stump shows the RACE `limb replacement` record whose `slot` is 50 + k (`uses_limb_replacement`); a crushed
  limb attaches no mesh. Hidden body vertices: [characters.md](../characters.md#hidden-body-parts-verified--140075600--140071f90--140531cc0-hlsl-skinhlsl).
- Meitou in play: fitted items sit in the inventory's `*_replace` sections (the limbs window), weigh nothing, are not traded and
  are saved only through MEDICAL_STATE `limbs`. Dropping a LIMB_REPLACEMENT whose `slot` is 50 + k on slot k fits it when limb k is a
  stump; lifting it out removes it (a right click puts a carried one on its stump or takes a fitted one off). The original cannot fit over a crushed limb: its slot is disabled
  ([limbs window](ui-screens.md#limbs-window)); fitting and removing are instant, with no medic or timer (`FUN_140645690`, `FUN_1400d5a30`, **Verified** in the decompile).

| Slot | Fields | Stat numbers |
| --- | --- | --- |
| all | `swimming mult`, `HP` (/`HP 1`), `unarmed damage bonus` (/`... 1`), `overall mult` | 23, part HP |
| arms | `strength mult`, `dexterity mult`, `thievery mult` (applied to both 10 and 37), `ranged mult` | 1, 18, 10 and 37, 35 |
| legs | `athletics mult`, `stealth mult` | 17, 16 |

### Races

RACE fields for this subsystem (loader `FUN_14042efa0`; **Verified** field by field: the reader's store offset
and a consumer that reads the same offset):

| Field | Meaning | `RaceData` offset | Consumer |
| --- | --- | --- | --- |
| `heal rate` | multiplier of part healing and blood recovery | +0x58 | `FUN_140652000` |
| `bleed rate` | multiplier of blood loss (0 = bloodless) | +0x5c | `FUN_14064f300`, `FUN_140646140` |
| `heal stat` | `StatsEnumerated` of the first-aid skill | +0x88 | `FUN_140649a00`, `FUN_1408c6130` |
| `hunger rate` | multiplier of starvation speed | +0x70 | `FUN_1408837a0`, `FUN_14064da70` |
| `min blood`, `max blood` | blood at weakest / strongest | +0x50 / +0x54 | `FUN_1406440a0` |
| `speed min skill`, `speed max skill` | run speed at athletics 0 / 100 | +0x4c / +0x48 | `FUN_140883db0` |
| `walk speed`, `swim speed mult`, `swim offset`, `water avoidance` | movement; water avoidance a becomes a + 1 if a >= 0 else 1 / (1 - a) | +0x64, +0x60, +0x68, +0x6c | `FUN_140886460`, pathfinding |
| `vision range mult` | detection range: outdoor vision range = 1500 x this ([perception.md](perception.md#sight)) | +0x74 | AI |
| `is robot`, `swims`, `carriable`, `single gender`, `gigantic`, `vampiric`, `no hats`, `no shirts`, `no shoes`, `cant enter buildings` (stored inverted) | flags | +0x7c, +0x79, +0x7a, +0x7b, +0x78, +0x7d, +0x7e, +0x7f, +0x80, +0x9c | many |
| `extra attack slots` | added to CONSTANTS `max num attack slots` (1) | +0x84 | combat (`FUN_1406658c0`, ...) |
| `self healing` | slow healing without bandaging (read per part) | per part | `FUN_14064dd10` |
| `strength`, `dexterity` | stat multipliers (starting value and XP rate) | map entries 1, 18 | all XP code |
| `stats good<N>`, `stats bad<N>` | x1.2 / x0.8 for the listed stat numbers | map at +0xa0 | all XP code |
| `weather immunity<N>` | WeatherAffecting kinds ignored | set at +0xe0 | weather |
| `special food` | extra ITEMs the race can eat | set at +0x00 | eating |
| `vampiric` | "when eating, sucks nutrition instead of eating limbs" (editor text) | +0x7d | eating |
| `combat anatomy`, `severed limbs`, `limb replacement` | body parts, dropped limbs per slot, default prosthetics | references | see above |


### New-game advanced options that change these mechanics

**Observed**, UI constructor `FUN_1409151d0` and writer `FUN_140912f80` (nine floats copied to the globals at
`142133578..142133598` when options are applied). The exe image holds zeros there, so defaults are set at game
start, not statically readable (**Unknown**; the sliders' ranges are in the binding).

| Option label | Global | Slider range | Effect found |
| --- | --- | --- | --- |
| Hunger time | 142133594 | 0.25 to 8 | multiplies `starvation time` |
| Chance of death | 142133578 | 0.5 to 4 | multiplies the bleed rate (`FUN_140646140`) and the wound degeneration (`FUN_14064f5c0`); it does **not** touch starvation: the starvation time reads the *Hunger time* float, which is at +0x1c of this block (**Verified**: addresses of the slider bindings in `FUN_1409151d0` and the writer `FUN_140912f80`) |
| Global damage multiplier | 142133580 | 0.5 to 4 | multiplies damage and combat XP ([combat.md](combat.md#global-tuning-the-constants-record)) |
| Building speed / Research speed / Production speed | 142133584 / 14213358c / 142133590 | 0.5 to 2 | construction, research and production rates, [buildings-production.md](buildings-production.md#construction-tick) |
| Number of nests multiplier | 142133588 | 0.5 to 4 | scales the nest count of the area spawner, [factions-squads-towns.md](factions-squads-towns.md#population-and-contents) (**Observed**: that doc's "global density float" `DAT_142133588` is this slider; [combat.md](combat.md#global-tuning-the-constants-record) lists the binding) |

### CONSTANTS used by this subsystem

One loader (`FUN_14086b2b0`) reads the "GLOBAL CONSTANTS" record (`110-gamedata.quack` in `gamedata.base`) into the
block at `142133dd0` (`field address = 0x142133dd0 + 4 * index`). **Verified**: for every row below the loader's
read and at least one consumer found through the data cross-references; base values from a probe over CONSTANTS
(editor defaults in brackets). "Stored" is the scaling the loader applies.

| FCS field | Base | [Editor] | Stored | Used by |
| --- | --- | --- | --- | --- |
| `damage multiplier` | 0.65 | [8.0] | multiplies the damage values below | combat |
| `bleed rate` | 0.01 | [0.01] | x0.1 | `FUN_14064edc0`, `FUN_1406508d0` |
| `immediate blood loss` | 0.2 | | x1 | `FUN_14064f300` |
| `bleeding clot rate` | 0.0085 | [0.0005] | x0.1 | `FUN_140646140`, `FUN_1406464c0` |
| `extra blood loss from bodyparts` | 1 | [1] | x1 | `FUN_140646140` |
| `blood recovery rate` | 0.4 | [0.3] | x0.1 | `FUN_140652000` |
| `bodypart degeneration rate` | 1.5 | [1.0] | x1 | `FUN_14064f5c0` |
| `degeneration mult 1` / `99` | 1.7 / 0.03 | [3 / 0.5] | x1 | `FUN_140652000` |
| `knockout mult 1` / `99` | 3 / 0.75 | [3 / 3] | x1 | `FUN_1406447d0`, `FUN_140644980` |
| `knockout time base` | 20 | [30] | x1 | same |
| `min` / `max toughness ko point` | 10 / 85 | [10 / 99] | x1 | `FUN_140643ae0`, `FUN_140644320` |
| `stun recovery rate` | 1 | [1] | x1 | `FUN_14064f5c0` |
| `blunt permanent organ damage` | 0 | | x1 | `FUN_14064f300`, `FUN_140644a70` |
| `heal rate mult` | 0.25 | [1.0] | x1 | `FUN_14064f5c0` |
| `resting heal rate mult` | 2 | [1.0] | x1 | `FUN_14064f5c0`, `FUN_140652000` |
| `medic speed mult` | 3 | [1.0] | x1 | `FUN_140649a00`, `FUN_140649d50`, `FUN_14064d800` |
| `medkit drain 1` / `99` | 20 / 1 | [1.0 / 0.05] | x0.1 | `FUN_140644250` |
| `robot medic speed mult` | 0.33 | [1.0] | x1 | `FUN_140644250`, `FUN_140649a00` |
| `robot wear rate` | 1 | [1.0] | x1 | `FUN_14064f300` |
| `XP rate medic 1` / `99` | 15 / 150 | [10 / 300] | x1 | `FUN_1408c6130` |
| `starvation time` | 60 | [24] | x1 | `FUN_1403e7620` |
| `fed recovery rate mult` | 3 | [4.0] | x1 | `FUN_14064da70` |
| `bed hunger rate` | 0.33 | [0.4] | x1 | `FUN_1408837a0` |
| `encumbrance hunger rate` | 0.7 | [0.7] | x1 | `FUN_1408837a0` |
| `food quality mult` | 0.5 | [1.0] | x1 (stored as is; it scales a food item's `charges`. It is `food price mult`, not this field, that is multiplied by `global price mult`: [economy.md](economy.md#3-constants-price-block)) | item constructor `FUN_14075faf0` |
| `exp gain multiplier` | 3 | [3.0] | x0.25 | all XP |
| `skill diff xp 2x bonus` / `0x penalty` | 10 / 25 | [10 / 20] | x1 | `FUN_1408c5fa0` |
| `min strength xp mult` | 0.1 | [0.05] | x1 | `FUN_1408838e0` |
| `weight strength diff 1x` / `max` | 20 / 1 | [20 / 1.5] | x1 | `FUN_1408838e0`, `FUN_1408839a0` |
| `xp rate strength` | 0.5 | [1.0] | x1 | `FUN_1408c6980` |
| `xp rate strength from walking` | 0.5 | [1.0] | x1 | `FUN_1408c60c0` |
| `xp rate athletics` | 1 | [1.0] | x0.5 | `FUN_1408c6660` |
| `xp rate toughness` | 1.33 | [1.33] | x1 | `FUN_1408c68e0`, `FUN_1408c6980` |
| `encumbrance base` | 15 | [10] | x1 | `FUN_140884780` |
| `carry weight mult` | 1.2 | [1.2] | x1 | `FUN_140884780` |
| `carry person weight` | 30 | [40] | x1 | `FUN_1405c89d0` |
| `weapon inventory weight mult` | 0.5 | [0.5] | x1 | `FUN_140889cd0`, `FUN_140898830` |
| `max num attack slots` | 1 | | x1 | combat |
| `unarmed damage mult` | 0.8 | [1.5] | x1 | `FUN_1408868b0` |
| `damage resistance min` / `max` | -0.65 / 0.65 | | x1 | `FUN_140644770`, `FUN_1406508d0` |
| `base block chance`, `block chance increase` / `reduction per 10levels` | 70, 12, 15 | | x1, x0.1, x0.1 | `FUN_140885d80`, `FUN_1408862d0` (blocking, not decoded) |
| `attack chance factor` | 0.05 | | x1 | `FUN_140665370` |
| `minimum lockpick chance` | 5 | | x0.01 | lockpicking |
| `appearance random deviation percentage` | 0.4 | | x0.5 | [../characters.md](../characters.md) |

## Unknowns

- What the `dt * 0.05` factor and the small `0.04` / `0.002` / `1.2` constants in the medical update stand for.
- The consumers of LOCATIONAL_DAMAGE `affects skills` / `affects move speed`.
- In one #46 run (20261021-194120) perception and friendly fire read 0.7414 healthy and 0.6896 with the head at
  0.75; both fit one extra factor of about 0.614 on m: light (p0 0.25) or dust (`FUN_1406439f0`). Crossbows
  (light, no dust) would tell them apart.
- The idle hunger factor CharStats+0x178; whether `food quality mult` scales raw food (function 15).
- Which callers feed `FUN_1408c7250` / `FUN_1408c7310` for which activity (`DAT_142133794` is the scaled frame time, [game-loop.md](game-loop.md#the-three-time-values)), and the 0.1 floor / 6x cap of the skill-difference factor
  (not in `FUN_1408c5fa0`; maybe clamped by its callers). Meaning of XP event 6.
- The defaults of the new-game option block (zero in the exe image).
- RACE `combat move speed mult`, animal CHARACTER fields.
- Which of the two ints of a `combat anatomy` reference is HP and which the hit weight (the part constructor
  `FUN_14064d0e0` takes the weight into +0x38, the HP into +0x54 and a scale into +0x58, but the call arguments
  are hidden in the decompile, so the assignment is inferred from consumers and data), and the meaning of the
  character slot +0x248.
- What the Hive Queen and Crimper (`is robot` races that eat and have blood) are meant to be in the flag semantics:
  whether `is robot` alone selects wear and robotic healing for them (it does in the part code, **Observed**).
- Damage kinds 1, 5 and 6 passed to `FUN_140666780` / `FUN_1408c68e0`.

## Expression reference

- Levelling: `0.1 * 0.75 = 0.075`
- Hunger and starvation: `T = starvation time * HungerTime`; `moved = min(buffer, dt / T * k * fed recovery rate mult * race hunger rate)`
- Sleep, beds and resting: `Character+0x2f8 == 1`
- Blood and bleeding: `blood_max = min blood + (max blood - min blood) * s * k * z`
- Per-part state: `M = base * mult * scale - w`; `== 1`; `== 2`
- Damage into a part: `Dismemberment=1`
- Degeneration, healing, stun: `D = clamp(((M - h - b) * 100 / M - 20) / (90 - 20), 0, 1)`; `degMult = lerp(degeneration mult 1 (1.7), degeneration mult 99 (0.03), toughness / 100)`; `heal = min(b, dt * 0.05 * heal rate mult (0.25) * R)`; `h <= M`; `R = (M / 100) * A`
- Knock-out, coma, death: `k = lerp(knockout mult 1 (3.0), knockout mult 99 (0.75), toughness / 100)`
- First aid, medkits and robot repair: `h + b <= M`; `rate = lerp(1, 15, min(skill, kit quality) * 0.01) * medic speed mult (3)`
- Encumbrance and carrying: `C = carry weight mult * strength * strengthInjuryMultiplier + encumbrance base` (strength's whole stat multiplier); `e = clamp((C - 0.05 W) / (0.95 W), 0, 1)`; `W <= C`
- CONSTANTS used by this subsystem: `field address = 0x142133dd0 + 4 * index`
