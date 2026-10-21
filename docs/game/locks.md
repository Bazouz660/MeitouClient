# Locks, doors, cages, beds and shackles

Original-game rules for lockable objects and the tasks that use them. Task tables: [ai-tasks](ai-tasks.md);
carrying into beds/cages: [carry-loot](carry-loot.md); crime amounts and bounties: [economy](economy.md#crimes-bounties-and-prisoners);
lockpicking stat and XP core: [character-stats](character-stats.md). As built: [simulation](../simulation.md#cages-beds-and-locks). Function addresses are kenshi_x64.exe 1.0.68.

## Data

| Object | How it is recognised | Fields |
| --- | --- | --- |
| Cage | BUILDING `functionality` -> BUILDING_FUNCTIONALITY `function` 8 (BF_CAGE) | Base data: `Prisoner Cage` (hardness 50), `Spiked Cage` (25) use functionality `Cage`; `Prisoner Pole` (30) uses `Cage- prisoner post`; rebirth.mod `Peeler Machine` uses `skin peeler` (also 8) |
| Restraint (pole, peeler) | a cage whose functionality has `restrains movement` true | `Cage- prisoner post`, `skin peeler` |
| Bed | `function` 6 (BF_BED), 25 (BF_SKELETON_BED) | `Bed`, `Old Bed` (`sleeping`), `Camp Bed`, `Fish Bed` (`sleeping on floor`), `Skeleton Repair Bed` |
| Door | a BUILDING listed in another building's `doors` list (35 base records; [pathfinding](pathfinding.md) for `is door`) | `door type` (DoorMaterial: sound only), `always locks`, `lock level bonus`; motion fields `door axis/move dist/move speed/comes out` |
| Lockable container | BUILDING `has lock` true (all 10 also `has inventory`) | `Safe` (bonus 25, hardness 90), `Metal Chest` (10, 60), `Ancient Safe` (10, 60), `Secure Weapon Cabinet`, `Crossbow Locker` (15, 60), `Weapon Display` (10, 30), `Bandit Loot Store`, `Weapon Cabinet` (0, 30), `Secure Wooden Chest`, `Metal Box` (-15, 15) |
| Shackles | ARMOUR `is locked` true | `Prisoner Shackles` (`SHACKLES`), `Hive Prisoner Shackles` (`SHACKLES-BODY`): lock level 20, hardness 20; `Faulty Prisoner Shackles`: 5, 10 |

Probe over the merged base data (gamedata.base, Newwworld, Dialogue, rebirth): 603 BUILDING, 103 FACTION (**Verified**).

- BUILDING_FUNCTIONALITY `occupant selection` (beds, cages): selecting the building selects its occupant (editor text). Cage
  animations: `Cage` idle `stand 1` / `sitting idle`, dazed `sitting dazed`, KO `foetal`; the post uses `stand pole` for all slots
  including `animation lockpick`. Beds: `sleepinbed`. `use range` 25 cages, 15 beds (**Verified** data).
- FACTION `doors lock level` (default 40), `lock level random` (default 10): 78 factions at 40/10; others 20..80
  (Shinobi Thieves 80, Nameless 75, Traders Guild / Slave Traders / Skeleton Legion 55, Holy Nation 25) (**Verified** data).
- `cages lock level` and `containers lock level` exist in fcs.def and the data but are **never read**: the strings are absent
  from kenshi_x64.exe; only `doors lock level` and `lock level random` occur (**Verified**: byte search of the exe). Every
  building lock uses the doors value.
- SQUAD_TEMPLATE `initial door state` (DoorState CLOSED 0, OPEN 1, LOCKED 2, BROKEN 3): 959 templates, 935 CLOSED; LOCKED 18
  (labs, outposts, HQs, gate guards), BROKEN 4, OPEN 4 (**Verified** data). `public day` is read into the home building
  (`FUN_14057f1f0`, building +0xc8, saved as `is public day`); **`public night` is never read** (string absent from the exe,
  **Verified**).

## Lock object

One structure (0x28 bytes) serves doors, lockable buildings, cages and shackles (**Verified**: the same constructor
fields in the door constructor `FUN_14029fb40`, the functional-building constructor `FUN_1402a0900` and `FUN_14029a160`):

| Offset | Meaning |
| --- | --- |
| +0 | level, int, clamped to 0..100 |
| +4 | hardness, float (cut/break resistance) |
| +8 | owning object (building or item) |
| +0x10 / +0x14 | retry interval bounds, 5 and 20 s |
| +0x18 | pick time accumulator |
| +0x1c | time of the next pick roll, starts at 5 s |
| +0x20 | locked flag |
| +0x21 | broken flag |

**Effectively locked** = locked flag set, broken flag clear, owner not destroyed (owner virtual +0x308) and level >= 1
(`FUN_140297d60` and every consumer; **Verified**). A broken or level-0 lock is open whatever its flag.

Creation (**Verified**, `FUN_14029d860`): level = FACTION `doors lock level` + uniform int in [-r, r] (r = `lock level random`)
+ the BUILDING record's `lock level bonus`, then clamped (doors: values below 2 become 1; buildings: below 0 becomes 0) and
capped at 100. The faction is the building's owner faction at creation. A building gets a lock when its `function` is 8
(every cage and pole, regardless of `has lock`) or `has lock` is set; the lock starts unlocked. Hardness: containers and
cages use their own `hardness`; a door reads `hardness` from the **parent** building's record (the house, e.g. 40 for
houses, 20 swamp/old, 60 outposts), its bonus from the door's own record (**Observed**: argument traced to the parent in
`Zones_CreateDoor`). Shackles (`FUN_14089a440`): level = ARMOUR `lock level` clamped 0..100, hardness = ARMOUR `hardness`,
locked at creation (no faction term).

Save sample (autosave of the owner's game, 51 zone files): `lock lvl` values span 0..100 with most at 15..50 (**Observed**).

## Lockpicking (Task_PickLock 76, Task_PickLock_Shackles 201)

- **Chance** (`FUN_140297dc0`, **Verified** by disassembly): with `L` = lock level and `s` = effective lockpicking skill,
  `p = 0.9` if `s >= L`, else `p = 0.9 / 2^((L - s) / 10)`. A character imprisoned in a cage (`Character+0x2f8 == 2`) uses
  `s - 10` for the roll and the shown chance; the skill condition does not subtract it (**Verified**).
- **Allowed** (conditions 166 `iHaveSufficientLockSkill`, 167 `...ForShackles`; `FUN_1408ca230`, `FUN_1408ca120`):
  `p > minimum lockpick chance x 0.01` (CONSTANTS 5 -> 0.05; strict). Otherwise "I'm not skilled enough..." (**Verified**).
  At 0.05 the cutoff is `L - s > 41.7`.
- **Rolls** (`FUN_140297e50`, **Verified**): each task update adds the character's update time (`Character+0xc4`, the
  game-speed-scaled seconds since its last full update, see [game-loop](game-loop.md#the-character-update-budget)) to the accumulator. When it passes the
  next-roll time it resets to 0 and rolls `rand() < p`: success clears the locked flag; failure draws the next interval uniform
  in [5, 20] s. The first roll comes after 5 s. Expected time to open is therefore about `5 + 12.5 x (1/p - 1)` s.
- **Animation**: the cage functionality's `animation lockpick` when imprisoned, else `medic` (`crawl use` when crawling),
  played at speed `clamp(0.01 x skill, 0.55, 2.0)` (**Verified**, speed is cosmetic: it does not change the roll timing).
- **XP** (`FUN_1408c62e0`, **Verified**): after each roll, stat 37 gains `k x map[37] x ((101 - stat) / 101)^2` with
  `k = 1.2` on success, 0.24 on failure. The lock level is passed but unused.
- **Messages/sounds**: "Lockpicking (Chance {p}%)" on the bar, "Pick success!" / "Pick failed" floating text,
  `Character_Unlock_Success`. Wwise events: [audio](../audio.md#characters-footsteps-combat-voices).
- **Target** (`Task_PickLock` slot 5): a building handle (a door resolves to its lock-owning object) or a **character**, meaning
  that character's cage (only when they are in a building prison). Shackles: the item handle, or a character, meaning their
  worn shackles. Self-targets are allowed: the caged character picks its own cage, a shackled one its own shackles (both
  task rows carry the -10 penalty when caged). Task list B also needs `hasOneWorkingArm` and no weapon equipped.
- **Crime** (**Observed**: `FUN_140852c80`, the pending-crime setter of [economy](economy.md#116-booking-factions-and-police-hits-observed),
  called on every update of the task, before the roll): a building owned by another faction -> code 2 (Burglary) against the
  owner faction; a cage holding a character, or another character's shackles -> code 7 against the captive's owner faction
  (character `+0x1a0 -> +0xe0`). economy.md lists code 7 as "Terrorism" (from the game's name table); which label the game shows for freeing is **Unknown**.
- **Success side effects**: the owner squad's "house locked" flag is cleared; a freed character gets relation event 0xd from
  the picker (0xe on failure) (**Observed**).

## Cutting and breaking (285 / 185 cut, 286 / 186 break)

On shackles the context-menu verbs are "Unlock Shackles" (201), "Use Tools" (185) and "Use Strength" (186) (`FUN_14079b2d0`, **Observed**).
A locked shackle cannot be unequipped: "This item is locked in place and cannot be removed. You can try to pick the lock, find a saw, or use brute strength."

Same targets as picking; Task_CutLock / Task_BruteForceLock share the shackle tasks' update (**Verified** vtables).

- **Cut** (`Task_CutShackles` slot 2, **Observed**): needs `hasTools` (state 122). Each update: hardness -=
  `clamp(0.01 x a x b, 0.05, 0.2) x dt x 2`, where `a x b` is the strength-based product of the break test below. At <= 0 the
  best tool is consumed (a tool: ITEM `item function` 9, e.g. Hacksaw; `hasTools` `FUN_1405989e0` tests item `+0x124 == 9`, **Verified**) (aborts if none), the lock's broken flag is set, "Shackles broken!" (also for locks).
  The bar shows hardness x 10. Same crime rule as picking (2 for own-target / buildings, 7 for another character).
- **Break** (`Task_BruteForceShackles` slot 2, **Observed**): instant; succeeds when effective strength x stat scale > 3 x hardness,
  else "I'm not strong enough to do that". Sets broken, "*Crunck*".
- **Sounds** (**Verified**, strings and callers). Slot 1 of Task_PickLock (also Task_PickLock_Shackles), Task_CutShackles (also Task_CutLock) and Task_UnlockDoor posts the `Character_Unlock` loop on the character; Task_BruteForceShackles has no slot-1 sound.
  `Character_Unlock_Success` comes from slot 2 when a roll, cut or break succeeds. Slot 3 (the task's end, success included) of Task_PickLock and Task_UnlockDoor posts `Character_Unlock_Fail`, which only stops the loop;
  Task_CutShackles slot 3 only removes the progress bar, so what ends an interrupted cut's loop is **Unknown**. Task_CuffTarget (putting shackles on: "Restraining" bar, `medic` animation) posts no event.
- **Bash door** (81/226/287, `Task_BashDoor`, update `FUN_140348c10`, **Observed**): needs a weapon (animals use their natural
  one); humans play `stonemine`, the blow lands at clip progress 0.6. Each update books Burglary (2) against the owner. A blow
  (door vfunc 62, `FUN_14029d690`): the attacker's damage packet, x0.25 for an animal (vfunc 0x248), then
  `f = clamp(2 x blunt + 0.5 x cut, 20, 60)`; the door lock's hardness -= 0.015 x f. At <= 0 the door is broken, its lock
  unlocked and the door opened, sound `Break`. The task ends once the door is broken, open or opening. No AI package lists the
  bash tasks: the planner starts them (when, **Unknown**).

## Doors

- Door object fields (**Verified**: save writer `DoorStuff` slot 148 / reader 149): `state` (+0x380: 0 closed, 1 open,
  2 opening, 3 closing; this is not the DoorState enum), `doa` open amount (+0x37c, 0..1), `broken` (+0x3ec), `gatecode`, `wants lock`
  (+0x384), and the lock (`lock lvl`, `locked`, `broken`, `hardness`). `wants lock` starts from `always locks` (gates).
- **Motion** (**Verified**, constructor `FUN_14029fb40`, update slot 28 `FUN_140299030`, pose `FUN_140298d10` by disassembly): doors
  slide, they never swing. Open (`FUN_140297040`) works only from state 0 -> 2 and tests no lock; close (`FUN_140298c50`) only from
  1 -> 3, not on a destroyed door. Each frame `doa` moves by frame time x `door move speed` (+0x388, default 0.5 before the record
  is read); at 1 the state is 1, below 0 it is 0 and, with `wants lock`, the lock is set (`FUN_1402969f0`). Sounds `Door_Open`,
  `Door_Close`; `Door_Shut` at both ends. Leaf offset from its closed pose, `d` = `door move dist`, `o` = `door comes out` (both signed):
  `o = 0`: `slide x d x doa`; else `t = |o| / (0.25 |d| + |o|)` and `doa < t`: `out x o x doa / t`, otherwise
  `slide x d x (doa - t) / (1 - t) + out x o`. `slide` is the door collision shape's local axis `door axis` (0 X, 1 Y, 2 Z); `out`
  is the shape box's thinnest other axis, sign-flipped by a dot test against the shape position (`FUN_14029cc90`; the reference
  point is not decoded, **Observed**: Meitou points it away from the building origin, which matches the Storm House doors in game).
  Data (35 door records): houses slide sideways (axis 0, 16..30 units, speed 1), gates up (`Concrete Gate Door` axis 1, 85) or along
  Z (defensive gates 65..100, speed 0.2..0.25); 14 TwoStorey doors step out 0.6..13 units first.
- **Who opens and closes** (**Observed**, callers of the two functions): opening: Task_OpenDoor (its start and update; the update
  fails on a locked lock), the door panel button, a broken lock (bashing), dialogue effect 0x36 (the speaker's building's door).
  Closing: Task_CloseDoor, Task_LockDoor and the door panel. The planner opens doors for walks: Moving (1, 29, 67) lists states 59
  `DESTINATION_IS_ACCESSIBLE` and 60 `LOCATION_IS_ACCESSIBLE` in list B, which tasks 85 / 84 (Task_OpenDoor) make true by opening
  the door ([ai-tasks](ai-tasks.md)). Nothing closes a door behind a walker: only the lock tasks list a closed door (`!52`), and no
  AI package lists tasks 73/75 (**Observed**: TaskData and package census). How states 59/60 pick their door is **Unknown**.
- **Door panel** (player's building, the HUD lines of a selected house: [ui-screens](ui-screens.md#selected-building)): open/close button `FUN_140546fb0`: an open door closes; otherwise an effectively locked
  one is unlocked first, then opened (**Verified**).
- **Player lock toggle** (`FUN_140547060`, **Verified**): flips `wants lock` (sound `Lock_Building` / `Unlock_Building`); locking a
  closed door sets the lock at once, unlocking clears it; an open door locks when it next closes (the update above).
- **Navigation**: the navmesh treats a door as open once `doa >= 0.6` (`FUN_140298d10` -> `FUN_1403a6070`); closed faces are
  blocked for every filter ([pathfinding](pathfinding.md)). A locked lock makes `Task_OpenDoor` fail ("It's locked.") for anyone,
  owner included: the owner must unlock first (**Verified**). Whether the planner routes strangers around locked doors is **Unknown**.
- **Owner lock/unlock** (77/79/162/198, 78/80/140; B list `isObjectMine`): progress 0.3 per game second, so 3.3 s; ends with
  `Character_Lock` / `Character_Unlock_Success`. Locking closes the door first. No skill or level involved (**Verified**).
- **Residents** (`allDoorsLocked` 87, `FUN_140596bd0`): true when the character is in bed, or its squad's "house locked" flag
  is set (squad AI object +0xe8, set by Task_LockDoor when no door of the home is open, cleared by unlocking, picking or a
  non-player opening a door), else when the home building has no door that is open, broken or effectively unlocked
  (`FUN_1408cb1c0`, which also returns that door as the next target). Base AI_PACKAGE hours (**Verified** data):

| Package | Task | Hours |
| --- | --- | --- |
| Shop (leader and squad) | 155 Shop Lock all the doors | 0-6 |
| Civilian (leader) | 155 | 20-8 |
| addon- lockup and sleep at night | 155 | 22-6 |
| Shop catun | 155 | 23-7 (leader), 23-6 (squad) |
| Storage Guard, Slaver Storage Guard | 197 Lock all the doors - stay outside | 23-7 |
| Cage Warden (and copy) | 195 Lock all my cages | 22-24 (leader), 19-21 (squad) |

- **Initial door state** (`FUN_140553210`, when the home building is assigned to a squad; **Verified**): CLOSED -> unlocked, not
  broken, closed; OPEN -> unlocked, open; LOCKED -> locked, closed; BROKEN -> lock broken, door broken. Saved buildings keep
  their saved values. The gate on building +0xa8 that allows it is **Unknown** (presumably "not restored from a save").

## Cages and beds

- **Entering** (`FUN_140330600` with a building, from Task_UseCage / Task_PutInSomething; **Verified**): `Character+0x2f8 = 2`,
  the prison handle at +0x300 (handle type +0x308 = 0 for a building), the character is moved to the cage position and placed
  at its node; AI movement flags 0x10 when `restrains movement` (pole), else 4. The cage's lock is set **locked** when the
  cage has one (all cages do). **Observed**: prisoners with a bounty for the cage owner get a sentence ([economy](economy.md#115-bail-and-sentence)).
  Unique characters are recorded separately.
- **Locked cage**: the occupant cannot leave; `Get out of cage` (133, 207) needs `!isDoorLocked`; tooltip shows the lock level
  or "The cage you are in is UNLOCKED. Nobody knows this but you" (`FUN_1405d4260`).
- **Leaving** (`Task_GetOutOfCage`, `FUN_1403554b0`): restores AI flags and pose. It is an escape (crime 0xc Escaping Prison
  against the cage owner, prisoner flag `+0x1a0 -> +0xe8`) unless the sentence has run out; the escape flag is forced on for
  cages whose owner faction is `not real` or has `fundamental type` 8 (**Observed**). Lifting a prisoner out of another faction's cage
  (`FUN_14034fbe0`): the lifter gets code 7, the prisoner code 0xc (**Observed**).
- **Release** (`Task_ReleasePrisoner` 110): clears the occupancy, "{1} has been released from prison." (**Observed**).
- **Beds** (`Task_UseBed`, `Task_GetOutOfBed`): `+0x2f8 = 1`, bed handle +0x300 ([carry-loot](carry-loot.md)); beds have no lock.
- Character save keys: `in something` (= +0x2f8) and `in what` (handle +0x300) (**Verified**, `FUN_1406280a0`); platoons
  save `imprisoned` (`FUN_140372ef0`).

## Containers (Meitou)

Interior items with `has inventory` (storage size from the record, 18 x 18 by default) are container fixtures with their own grid, locked or not (`has lock`).
A resident squad with `vendors` has its home's stock containers filled and unlocked by the building filler, trader or not ([building shops](economy.md#building-shops-observed-decompile));
the others start empty (what else the original stocks them with is **Unknown**). **Engine choices**, not Verified against the original: a right click with a selection walks the nearest
character to one (`OpenContainerOrder`, reach 20) and opens a storage window beside the inventory, closing when the opener walks 1.5 x the reach away; a locked one refuses
("It's locked."; pick it first); a building shop's containers refuse too, their stock being the shop's one merged inventory (`Shop.ForBuilding`). Items move with
`PutInMachine` / `TakeFromMachine` addressed by the fixture id. Contents are saved in Meitou's own GAMESTATE_BUILDING string `meitou container <layout index>` (the index of the fixture id; JSON list of items with cell, quality, level,
material, maker, charge and a bag's contents; absent while empty; `Locks/ContainerSave.cs`), the original's interior ids being unmapped. Like the locks, only into building states the
save already holds (zones visited), and the original ignores the key (**Unverified**). A lock on a stored shackle and prosthetic wear are not kept. Theft rolls
([carry-loot](carry-loot.md)) for taking from another faction's container are not modelled.

## Save keys

Building records in `zone/*.zone` (**Verified** keys by census of 51 zone files, **Observed** meanings): the building's own lock
as `lock lvl`, `locked`, `broken`, `hardness` (written for every functional building, lock or not); each door `i` with the
suffix `i`: `state<i>`, `doa<i>`, `broken<i>`, `gatecode<i>`, `wants lock<i>`, and its lock `lock lvl<i>`, `locked<i>`,
`hardness<i>`. `locked` is saved true only when the lock is effectively locked; on load, level and `locked` are taken only when the saved `lock lvl` is above 0.
Shackles: the item's INVENTORY_ITEM_STATE record carries the same four keys (`LockedArmour` vfunc 75 writes, 76 reads through
`uses_locked_hardness`, **Observed**), with the same load rule.

## Unknown

| Gap | Engine choice |
| --- | --- |
| Noise or witness alert from a failed pick (nothing traced besides the pending crime and sounds) | none beyond the pending crime |
| Whether KO'd, carried or restrained characters may pick (no explicit test besides task conditions) | require conscious, free arms, not on a pole |
| Which tool cutting consumes when several are carried | the highest-quality one |
| Exact strength terms of cut/break (`FUN_1406457e0(stats, 1, ...)` x stat-scale virtual) | effective strength x 1 |
| Whether an awake prisoner can be lifted out of an unlocked cage (`FUN_14034fbe0`'s callers not traced) | allowed; the carry rules then knock out a non-friendly one on the shoulder and set a friendly one down |
| Whether non-owners path around locked doors | a building whose doors are all effectively locked is closed for every path; closed unlocked doors are passable |
| `gatecode` | preserve it |
| Which door states 59/60 open for a walk | a walker whose next path legs cross a closed door's plane opens it; at a locked one it stops ("It's locked.") |
| What else sets or clears the squad "house locked" flag | the rules above |
