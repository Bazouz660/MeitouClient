# Carrying, rescue and looting

Original Task_Lift/PutDown/PutIn, carrying/medical flags, inventory transfer and witness research.
Fields/source addresses and unresolved cases retained below. Runtime: [simulation](../simulation.md#carrying-rescue-and-looting).
Crime prices/ledger: [economy](economy.md); body/load: [stats](character-stats.md).

## Carry and release

**Verified** carriable race gate (53-race probe), left-arm usable medical+0x166, left shoulder/
Bip01 L UpperArm attachment. A collapsed, missing or crushed left arm prevents carrying ([arms](combat.md#arms)); right arm irrelevant.
**Observed** target must resolve character and differ from carrier; already-carried targets
require permitted snatch/partner check. AI down-state gate and player context-menu eligibility
are different. Race carry-bone affects attachment; secondary character type unknown.

**Observed** placement each frame (`CharacterAnimal::vfunc_77` `FUN_1405cda20`, `FUN_1405b5980`, `FUN_1405b1a30`): the body's
*node* (not its root bone) gets position `carrierPos + carrierRot·boneModelPos + boneWorldRot·O` and rotation
`boneWorldRot · q(w .70710677, x .70710677) · q(w .99254614, z .121869296)` (90° about x, ~14° about z); the `carry me` clip plays inside
it. Bone = `AnimationClass+0x1b0` (`attachRootToMastersBone`, KenshiLib 1ce4a16), set by the attach `FUN_1405ced90`. `O` lerps by
`t = 2·characterHeight_0to1` from (-1.4,0,1) to (-1.9,-1,2.6) (t<1), then to (-3,-0.4,5); `characterHeight_0to1 = (Height − 80)·0.025`,
0.5 by default (Appearance+0x18c, `FUN_1405334d0`). Gated by `Character+0x1a9`, `AnimationClass+0x1c0` and vfunc 0x248 = 0 (meaning
**Unknown**); otherwise the body is just put at the carrier's position.

Lift sequence/clip/approach, reciprocal partner handles, weapon/ragdoll/state changes and unlink
must stay ordered. **Verified** carry load is CONSTANTS30, not target's weight/race/inventory;
mode0 raises to1 without a separate speed-product carry factor. Dropping on arm loss/KO/death,
put-down and bed/cage placement are different state transitions. Bed/cage occupancy and release
must maintain both character and object links. Rescue target finders differ for squad/player/
NPC/leader and available safe beds; do not turn every pickup into kidnapping.

## Theft and witnesses

**Observed** pending crime stores code, victim faction/handle, timer and booked state. Own/lawless
factions and police/prisoner exemptions gate creation. Loot permission for downed targets checks
slave/prisoner/bounty/bandit/police/diplomat/own-property branches, not just consciousness.
**Verified** transfer callsites disagree: right-click conscious→Theft3, unpermitted down→Looting10;
drag-drop unpermitted down-character→Theft3, other source→Looting10. Ordinary loot UI path unknown.
Ground pickup can raise Theft inside another faction's building.

**Observed** steal probability: character1.1*thievery/100, equipped/slot multipliers0.75 each;
container0.3+1.95*thievery/100*t, t=lerp(0.5,1,clamp(distance/200)), half in same building,
sleeping/lying doubles, different floor*1.5. Unconscious/dead/carried/imprisoned watcher (or down
thief) guarantees success, but pending crime still exists. Caught creates Theft timer30,
a reaction (no faction relation event: none of the slot-5 callers in [factions 3.5](factions-squads-towns.md#35-what-fires-relation-changes-observed-decompile) is a theft or loot path) and XP with high-probability/imprisonment/timer exemptions.

Witnesses must be eligible and care about victim for theft/fencing/looting/kidnapping;
assault/murder have separate branches. Ordinary witness reacts; only police-like witness with
faction+0x40 books bounty. Kind7 is a squad-level suspect tag, not a faction relation event.
Friendly/hostile/slave-owner/lawless/bandit faction gates matter. Carrying past witnesses has
its own victim/captor ownership checks.

## Stolen ownership

**Observed** item+0x140=from-owner, +0x160=current holder. Backpack uses its owning character's
faction. Same faction/lawless holder is not stolen; strict1 can use set/nonmatching original-owner
handle even if unresolved, strict0 also requires resolution. Dropping ownerless items can set
dropper as owner. The save's `ownedby` is item+0x140 (**Observed**, `Item` vfunc 75/76); preserve handles, not a boolean only.

### Character fields used here

Offsets on the `Character` object; all **Observed** unless the row says otherwise. The names are ours.

| Offset | Meaning | Evidence |
| --- | --- | --- |
| `+0x348` byte | carries someone (`isCarrying`) | set by the attach `FUN_1405cff90`, cleared by the unlink `FUN_1405ce1e0`, read by the carried-weight function `FUN_1405c89d0`: **Verified**, three functions agree |
| `+0x3d4` byte | is being carried | set by `FUN_1405ced90` (the body's side of the attach), cleared by `FUN_1405ce1e0` / `FUN_1405ccba0`; makes "ragdolled or carried" (`FUN_1407d1440`) true: **Verified** |
| `+0x380` handle (type `+0x388`, ids `+0x38c..+0x398`) | the carry partner: the body for the carrier, the carrier for the body | written on both sides by `FUN_1405cff90` / `FUN_1405ced90`, reset to the null handle (type 0xb) by the unlinks: **Verified** |
| `+0x378` byte | carry side, 0 right shoulder, 1 left | picks the bone `Bip01 R UpperArm` or `Bip01 L UpperArm` in `FUN_1405ced90` |
| `+0x350` string | side suffix of the lift clip name (`shoulder lift left` / `right`) | `Task_LiftSomething` slots 1 to 3 |
| `+0x2e0` | the RACE object; RACE `carriable` is its byte `+0x7a` | the RACE loader `FUN_14042efa0` stores it there and the lift task reads it: **Verified** |
| `+0x2e8` | the inventory | `FUN_1405c89d0` |
| `+0x2f8` int | special state: 0 normal, 1 in a bed (bed handle at `+0x300`, type `+0x308`), 2 imprisoned (cage or restraint) | `FUN_14032e2f0` sets and clears 1; the attach clears it and releases a restraint (`uses_restrains_movement_unique` `FUN_140330600`) when it was 2 |
| `+0x650` -> `+0x318` | the medical object; its byte `+0x165` = right arm usable, `+0x166` = left arm usable | section 1.2 |
| `+0x5bc` byte | dead | set by the death function `FUN_1407a6200` ("{1} is dead.", "{1} has died from blood loss.") |
| `+0x5b9` byte | unconscious (inferred) | no writer found; every reader pairs it with dead as "cannot act" (`FUN_1407944d0`, `FUN_140997c70`, `FUN_1408cdf50`, `isReadyForAction` `FUN_140998550`) |
| vtable slot 6 (`+0x30`), `CharacterAnimal::vfunc_6` `FUN_1405c9e10` | `isDown`: dead, `+0x5b9`, ragdolled or carried, or leg state (slot 109, `+0xe0`) of 3 or more | |

### 3.2 Goals and their target finders

TaskData (`FUN_140319a60`) holds a **target finder** at `+0x160` and a follow-up goal pair at `+0xc` / `+0x10` (**Observed**: the field writes; the
finders resolved from the thunks in the exe's bytes):

| Goal | Desire | Finder | Follow-up |
| --- | --- | --- | --- |
| `FIND_AND_RESCUE` 105 | `140599480` (exe bytes): 0 if CharStats `+0x12b` is set or the character carries, else 1 | `FUN_1408cd550` | - |
| `FIND_AND_RESCUE_IF_THERES_BEDS` 148 | `FUN_1405994c0`: 0 if CharStats `+0x12b` or carrying, else the bed search `FUN_1408cfe00(..., 1)` | `FUN_1408cd550` | 148, then **106** `FIND_BED_AND_PUT_IN` |
| `FIND_AND_RESCUE_LEADER` 142 | - | `FUN_140598540`: the squad leader (`platoon +0xa0`) when it is not the character itself and not dead | - |
| `FIND_BED_AND_PUT_IN` 106 | `FUN_1405a0d50`: 2 while carrying a body the carrier regards as friendly, whose slot `0x248` is null, that (for a police carrier, `FUN_140599600`) has no bounty with the carrier's faction, whose faction is not hostile to the carrier's (`FUN_1406b26d0`) and not in its town's list `FUN_140599390`; else 0 | none | - |
| `CARRY_WOUNDED_SLAVES` 168 | - | `FUN_1408cdf50` | - |
| `FIND_AND_KIDNAP` 132 | - | `FUN_1408cd5c0` (the character finder below, masks 0x4008 / 0x811, beds allowed) | - |
| `FIND_A_DEAD_GUY` 138 / 230 | - | `FUN_1408cfb00` / `FUN_1408ce170` (the nearest dead, ragdolled, uncarried body; `carriable` when asked) | - |
| `LOOT_THE_DEAD` 177 | AI slot 62: 1, 0 while character vfunc `0x368` (crawling or ragdolled) | `FUN_14099e680` (`FUN_14099ae10(ai, 0)`, [NPC looting](#33-npc-looting)) | - |
| `LOOT_FOOD_AND_STUFF` 131, `AQUIRE_FOOD_AT_HOMEBASE` 243 | - | `FUN_1408cb650`: a building, `BuildingFinder_LootFoodAndStuff` over a town (from the AI's +0xf0, else +0x18 → +0x88, **Unknown**; no characters): buildings with an inventory holding food (filter partly read, **Observed**) | 243's effect `gotAFoodItem` comes from 244 `Task_grabOneFood`, which takes one food item of the building's inventory |
| `LOOT_STOLEN_GOODS` 212 | - | `FUN_1408cf6a0` (calls the stolen predicate) | - |
| `PICKUP_INTRUDERS` 223 / 273 | - | `FUN_14099c110` / `FUN_14099c250` | - |

**Character finder** (`FUN_1408cd2d0(task, out, include, exclude, allowBed)`, **Verified** by decompile; rescue 105/148 call it with masks 1 / 0x4010 and `allowBed` 0,
kidnap 132 with 0x4008 / 0x811 and `allowBed` 1): walks the owner's sensed list, keeping entries whose category word has a bit of `include` and none of `exclude`
(the bits are **Unknown**); the nearest (squared XZ distance) passing all of these wins: the target is down (ragdolled or carried, `FUN_1407d1440`, or the byte
`+0x5b8`) or, only when `allowBed`, in a bed (`+0x2f8 == 1`); not carried (`+0x3d4`), not dead, special state 0 (not caged; bed 1 when allowed), not an animal
(slot `0x248` is `asAnimal`), and not in the squad's give-up list (`FUN_1402690e0`, a handle-pair list every finder tests; its writer was not traced). Rescue also requires
the target to share the owner's slot-11 object (probably its faction; **Observed**: tested when `include` has bit 1 and a global flag at `+0x112` (Rescue Allies, [squad AI options](ai-tasks.md#squad-ai-options)) is clear). So raiders also take sleepers out of beds. 132 is a
planner goal (A: 153 got a victim, B: not carrying, effect 48 carrying); 254 has no finder: its B list 153, 154 chains 132, then 112/281 find a cage (`FUN_1408d0770`) and 108 puts the
carried one in. What sets `Status+0xe9` ("innocent victim") was not found: it is saved with the slave state (`FUN_140626580` / `FUN_140626830`), cleared by the per-update
`FUN_140852a30` (not carried and in its town's bed, or carried by a friend) and read at the lift's release (`FUN_1405ce1e0`) and by `FUN_1406cc470` / `FUN_1407211b0`;
it is not part of target eligibility.

### 3.3 NPC looting

**Observed** (decompile of `FUN_14099ae10`, `FUN_14059a7b0`, `Task_Loot_AI` start `FUN_140355ad0` and update `FUN_14034d930`, the take `FUN_140342de0`; registrations in
`FUN_140319a60`). The goal that loots downed characters is **177** "Looting the dead": the data's goal "Looting corpses and food" (`5196-gamedata.base`, enum 177) in
"Patrol map (short range) LOOTING" and the Starving Bandits' packages that inherit it. 131 "Looting food" and 243 "Getting food" find buildings, not bodies. 177's effect is
`isSubjectInInventory` (3), provided by row 3 `PICKUP` "Collecting item" (`Task_Loot_AI`, B list `isItemLootable`, `isAtLocation`): the plan walks to the target, then loots it.

- **Finder** (`FUN_14099ae10(ai, animalsOnly)`; 177 passes 0). Two passes over the sensed list, each target within 100 units (squared 10000), not carried (`+0x3d4`),
  reachable (`AI::isDestinationAccessible`, `destinationNotBlockedByFortifications`), not in the squad's give-up list, and not short-term tagged 6 by the looter's squad
  (`FUN_140678960(c, target, 6)`, [squad memory](dialogue.md#squad-memory)); the first match wins (sensed-list order, **Unknown**). Pass 1: mask 0x10 / 0 (read as the dead). Pass 2: masks 0x4008 / 0x11 (the kidnap finder's,
  read as other factions' downed) and also: the target's faction is not the player's (`FACTION +0x250`) unless the global **Bandits loot the player** (`142133598`) is set, and
  `shouldIScrewThisGuyOver(looter, target)` (`FUN_14059a7b0`).
- **shouldIScrewThisGuyOver**, in order (later lines override): true when either is of NPC class 8 (Bandit; status `+0xdc`); false when both have the same class; true when the
  looter is class 2 (Military); false when looter vfunc `0x3f0`(target, 1) holds (**Unknown**); false when the looter is class 1 (Law enforcement); true when the looter's status
  `+0xe0` (a slave's owner faction) is the target's faction; false when the looter's squad has a love tag on it (long & 0xc), true with a hate tag (& 0x2b0); true when the target is dead.
- **Start**: flags `animal` (target vfunc `0x248`), `notPlayerSide` (target's faction is not the player's, or the target is dead; cleared when looter vfunc `0x68` `+0x90` > 0,
  **Unknown**), `foodOnly` (looter of NPC class 8, or its hunger floor, slot 0x348 ([free food](character-stats.md#free-food)), below 2.8), `wantsWeapon` (the looter has no weapon,
  `AI::hasWeapon`, and the target has one). A law-enforcement looter (`isLawEnforcement`) confiscates stolen goods instead (not decoded further). An animal target posts
  "{1} is butchering {2} for meat!!" when the target's faction is the player's.
- **Update**: plays `medic` (start: `squat T`, `medic`); a timer adds the frame time. With neither `foodOnly` nor `notPlayerSide`, past 0.8 s: a `wantsWeapon` looter takes one
  weapon of the target's hip and back sections and is done. Otherwise the take comes past 8 s (an animal target: 20 s):
  1. Weapons (item type 2) of the target's inventory, when the looter has no weapon equipped (`Inventory::hasWeaponEquipped`) or the target is imprisoned; a weapon whose
     slots the looter has all filled is left.
  2. Food: items of type 4 with `item function` 3, then 15, repeated until none moves; then the same from the target's worn backpack (function 3).
  3. With `notPlayerSide` and not `foodOnly`: every item (type 4) and armour (type 3), and every item of the backpack.
  An item goes into the looter's inventory, else its worn backpack, else back. Then the looter's squad tags the target short-term 6 (`FUN_1406744a0`) and the task ends.
- **Bandits loot the player** is the new-game option at block `+0x20`, saved as CAMERA bool `bl` (**Verified**: the reader `FUN_1403eef20` reads key `bl` into `+0x20`, the writer
  `FUN_140912f80` copies `+0x20` to `142133598`; the reader's default is 1, and `bl` is true in the owner's three readable original saves).

## Unknowns

1. The bed search (`FUN_1408cfe00`) is decoded for player-faction characters and, in outline, for others ([locks](locks.md#beds)); the meaning of the designated-building kinds is not.
   Where 106 (no target finder) gets its bed is **Unknown**.
2. The **sensed-list category bits** (`FUN_140853960` masks 1, 0x4010, 0x20, 0x210, 0x4008, 0x811, 0x10): which bit is ally, enemy, dead, slave;
   and the size of the sensed list, which is the search range of every finder.
3. The **status object at `Character+0x1a0`** (int at +0, saved as `slavestate`: 1 slave, 2 escaped slave; the setter `FUN_1405a4940` posts "{1} has been enslaved" for a free player-faction character and sets `+0x110` to 250 on 2; its callers: caging, the squad factory's `slaves`, slave processing (task 182), escapes, release and bail); its `+0x88`, `+0xe0` (a slave's owner faction); its class name, and what sets `+0xe9` (the "innocent victim" flag; see the character finder).
   Partly known (**Observed**, [economy 11.4-11.8](economy.md#115-bail-and-sentence)): `+0xe8` escaped-prisoner flag, `+0xea` set on release
   or bail and cleared by a new pending crime, `+0x110` the "recently wanted" marker (250.0).
4. The **writer of `+0x5b9`**, read as "unconscious" from its readers only.
5. **Which inventory transfer the player's usual loot takes** (drag and drop gives Theft for a down owner, the right-click transfer Looting): both
   are real, the UI events behind `FUN_140713d20` and `FUN_140715560` were not mapped.
6. **Witness timing**: how often `FUN_1408580d0` runs per sensed character, with what range and line of sight, and how the pending timer `+0x90`
   counts down (`FUN_140851f40` sets it; the decrement was not found).
7. `PUT_DOWN_CARRIED_DUDE_IF_THEY_CAN_WALK` (169): its "can walk" test. (Kind 4 of `FUN_14038ab50` is the bed's nearest type-4 node the body is placed at, [locks](locks.md#beds).)
8. Bed rent: decoded ([locks](locks.md#beds)); allied factions' beds are free. The town-type and `FUN_140621230` tests in it are **Unknown**.
9. A **despawn timer** for ground items.
10. The steal order type 0x1d of the ALT path (`FUN_1407fc440`): the taskType table says 29 is `MOVE_CUS_ORDERED`.
11. The **speed caps** of the `carrying left/right` movement clips (RACE `male/female carry ragdoll` are unused: [ragdolls](ragdolls.md)).
12. `FUN_14079c600`, called when the player picks up a non-player character, and **`CharStats+0x12b`** (set by slot 97 order kind 0xc), which turns
    both rescue goals off.
13. The terms and the random roll of `FUN_140644980` on the put-down path (section 2.2), and the relation test `FUN_1406b27b0` of "friendly".

## Expression reference

- The attach: `+0x2f8 == 2`; `+0x2f8 = 0`
- Weight and speed: `platoon +0x78 -> +0xb0 != 4`
- Dropping: `RagdollPart { WHOLE=1, RIGHT_ARM=2, LEFT_ARM=4, ..., CARRY_MODE=2048 }`
- The unlink `FUN_1405ce1e0`: `+0x240 = -1`; `+0x3d4 = 0`
- `Task_PutInSomething`: `+0x2f8 = 2`; `+0x20 = 1`; `+0x2f8 = 1`; `+0x2f8 == 1`
- Crimes for taking items: `+0x34 == 8`

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- 1.3 Sequence and clips (**Observed**, one function each)
- 1.4 The attach (`FUN_14034fbe0` -> `FUN_1405cff90` -> `FUN_1405ced90`, **Observed**)
- 2.1 The unlink `FUN_1405ce1e0(character, ragdollArg, keepInPlace)` (**Observed**)
- 2.2 `Task_PutDownObject` (`PUT_DOWN_OBJECT` 69, also goal 169) (**Observed**: `FUN_14034a230` start, `FUN_1403504f0` update, `FUN_1403509f0` end)
- 2.3 `Task_PutInSomething` (`PUT_SOMEONE_IN_BED` 99, `PUT_IN_CAGE` 108) (**Observed**: `FUN_140358170`, `FUN_140358670`, `FUN_140358cc0`)
- 4.1 What may be looted (`isItemLootable`, state 0xf, `FUN_14059dcc0`, **Observed**)
- 4.4 The stolen flag (`FUN_14079e550(item, strict)`, **Observed**; answers part of the economy.md Unknown)
- 4.6 Carrying people past witnesses (`FUN_1408549f0`, **Observed**)
