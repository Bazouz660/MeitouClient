# AI

Original AI_PACKAGE/AI_TASK/Blackboard/Tasker/GOAP research from binary, enum/schema and merged
records. [ai-tasks](ai-tasks.md) is the authoritative task/class/condition registry; [simulation](../simulation.md#ai)
describes engine approximations. Preserve squad requests versus per-character decisions.

## Packages and goals

Package slot/class/signal, tiers, target/ending, time windows, weights and world-state rules are
record data, not hard-coded task names. Blackboard maintains squad/home/targets/requests; loading
packages and choosing goals are separate operations. Default character tasks/jobs/player orders,
urgent reactions, unloaded-platoon jobs and dialogue event eligibility have distinct branches.
Do not assume every factory Task_Blank goal has no behaviour outside that factory.

**Observed** scoring FUN_14050df10: filter hour (start<end half-open, overnight otherwise), missing
dialogue event and already-satisfied A. score=desire*weight*crowdFactor^n*m, n same type/target
mates, m1.25 for current type/target/second hand. Effect-state evaluation can scale positive
desire. Sticky current task floors0.4 before commitment expiry, caps0.05 after; another owner
condition can add1.25 when score<1 (unknown). Drop nonpositive candidates; B-satisfied/no-effect
branch also drops. Resolve key ties by0.0001 increments. Desire, commitment and crowd factor per
type: [ai-tasks](ai-tasks.md#desire-and-commitment); exact A/B meanings remain unresolved.

The planner (`TaskPathfinder`, [ai-tasks](ai-tasks.md#planner), Observed) chains rows from a goal's effect and unmet B states to the
rows whose list A achieves them, best-first by desire, and queues the chain leaf first.
Squad toggles inject sleep/ditch/sit goals; slave obedience uses role4. Player commands and job
queues must coexist without erasing urgent state. Squad-shared sensing, retreat and off-screen
routing use [perception](perception.md), [population](factions-squads-towns.md), [pathfinding](pathfinding.md).

### AI_TASK

| Field | Values | Label |
|---|---|---|
| `enum` | `taskType` 0..288 (289 names in `fcs_enums.def`); 108 distinct types are used | **Verified**: probe over all 124 records, every value is a valid index. The record `Name` is free text (101 of the 124 are descriptive phrases, not the enum name) that reads as matching its enum, except rebirth record 1532845 "Find and kidnap (raider) urgent" (255 SIT_ON_THRONE) and "eating food on the ground" (14 IDLE), which are **Observed** mod quirks |
| `classification` | TP_FLUFF 5 records, TP_NON_URGENT 82, TP_URGENT 37 | **Verified** |
| `targeting` | SPECIFIC 99, SELF 11, LEADER 4, SQUAD_MISSION 9, HOME_GATE 1 | **Verified** (counts). Behaviour **Observed** (goal reader `FUN_1402704e0`): LEADER takes the squad leader's hand (`FUN_14079aea0`), SQUAD_MISSION the Blackboard's mission target (`FUN_140269b60`), HOME_GATE a Blackboard lookup (`FUN_14026b410`); SPECIFIC and SELF set nothing at load time (SELF is filled with the character's own hand where the task is created) |
| `ending` (TaskEndEvent) | NOTHING 115, REMOVE_MINE_ONLY 8, REMOVE_WHOLE_SQUADS 1 | **Verified** (counts), behaviour **Unknown** |

### Other records that touch the AI

| Record / field | Role | Label |
|---|---|---|
| SQUAD_TEMPLATE `AI packages` (also 1 of 11 SINGLE_DIPLOMATIC_ASSAULT records, entry (0,100,0)) | 838 of 959 templates; each entry (slot, ...) with slot 0..4 (838 at 0, of which 777 are (0,0,0) and 61 are (0,100,0); 98 at 1, 55 at 2, 8 at 3, 2 at 4), at most 5 packages per template. The slot is a priority: the Blackboard walks slots from the highest to 0. The reader (`FUN_140271620`) passes the entry's first int as the slot to the add-package function `FUN_14026c160`, then resolves every package's `Leads to` among the packages it just loaded (a `Leads to` target that is not one of the squad's own packages stays unlinked) | **Verified** (counts), **Observed** (priority role, `Leads to` resolution) |
| RACE `AI Goals` | 47 of 53 races; entries (SELF_PRESERVATION x30, HEAL_MY_LEGS x16, IDLE x9, EAT_A_RANDOM_DEAD_BODY x7, ...). Read by `FUN_1405094f0`: only val1 x 0.01 is used (no zero default; the data is mostly val1 = 100, i.e. 1.0), val0 is **not** read (the entries are mostly (4,100,0) or (0,100,0)), the hour window is fixed 0..100, and the target comes only from the legacy bool fields `target leader` / `target self` / `squad mission target`, which no current AI_TASK record has (AI_TASK has only `enum`, `classification`, `targeting`, `ending`), so the `targeting` int is ignored on this path | **Verified** (reader and data), **Observed** (data) |
| FACTION / FACTION_TEMPLATE `AI fallback` | Packages added at slot 0 (`FUN_140271fd0`); used by 1 FACTION record and 0 FACTION_TEMPLATE records | **Observed** |
| DIALOGUE_LINE `AI contract` (169 uses; val0 = hours), `change AI` (190 uses), `has package` (751 uses) | `change AI` replaces the squad's packages, `AI contract` runs one aimed at the dialogue target: [dialogue Effects](dialogue.md#effects) | **Observed** |
| AI_SCHEDULE | Empty in the base game set (0 records in the merged database); fcs.def says its `packages` list switches packages by hour (val0), but no runtime use was found | **Verified** (count) |

### Enums

| Enum | Values | Source, label |
|---|---|---|
| `taskPriority` | TP_JUST_ACTION 0, TP_FLUFF 1, TP_NON_URGENT 2, TP_URGENT 3, TP_OBEDIENCE 4, TP_MAX_SIZE 5 | **Verified**: the FCS editor's enum (decompiled, outside the repo; it is not in `fcs_enums.def`); `OrdersReceiver` holds two arrays of five lists (constructor `FUN_140507d20`, indexed at +0xa0 and +0x118 plus tier x 0x18 in `FUN_140510d60`); data uses only 1, 2, 3 |
| `taskType` | 289 names | `fcs_enums.def`; see [ai-tasks.md](ai-tasks.md) |
| `TaskTargetType` | SPECIFIC, SELF, LEADER, SQUAD_MISSION, HOME_GATE | `fcs_enums.def` |
| `TaskEndEvent` | NOTHING, REMOVE_MINE_ONLY, REMOVE_WHOLE_SQUADS | `fcs_enums.def` |
| `UnloadedPlatoonJob` | NONE 0, PATROL_TOWN 1, PATROL_SHORTRANGE 2, PATROL_LONGRANGE 3, GOHOME 4, TRAVEL_TARGET 5, TRAVEL_TARGET_FAST 6 | The FCS editor's enum (not in `fcs_enums.def`). The numbering of 2, 4, 5 and 6 agrees with the unloaded update's behaviour per value; 1 and 3 have no branch there (**Verified** by code, [off-screen world](factions-squads-towns.md#off-screen-world)) |
| `BlackboardSignalFunctions` | see the table below | **Verified**: the FCS editor's enum (not in `fcs_enums.def`) and the RTTI package classes built by the factory agree value by value |
| `EventTriggerEnum` | 0 EV_NONE, 1 PLAYER_TALK_TO_ME, 3 I_SEE_NEUTRAL_SQUAD, 13 UNLOCK_MY_CAGE_OR_SHACKLES, 15 I_DEFEATED_SQUAD, 30 CONTRACT_JOB_ENDED, 44 BOUNTY_SPOTTED, 45 ESCAPED_PRISONER_SPOTTED, 46 PRISONER_FREE_TO_GO, 51 BOUGHT_ME_FROM_SLAVERY, ... | The editor exe's numbering is authoritative. `fcs_enums.def` is a commented subset in another order (it starts with EV_PLAYER_TALK_TO_ME, no EV_NONE). Cross-check: the dialogue `dialogs` val0 histogram (event 3 x294, event 1 x220, 13 x147, 15 x87) and the binary firing 30 on contract end (`FUN_14026a110`) and 13/44/45/46/51 at the prison sites of [economy 11.5-11.8](economy.md#115-bail-and-sentence). Full list in [dialogue](dialogue.md#enums) (**Verified**) |
| `DialogActionEnum` (DA_*), `DialogConditionEnum` (DC_*), `DialogRepetitionEnum`, `CharacterTypeEnum`, `TalkerEnum`, `PersonalityTags` | Names and numbers. `DialogActionEnum` has unprefixed entries 41-48 (`CHOOSE_SLAVES_SELLING` .. `CHOOSE_ANIMALS_BUYING`, 43 `CHOOSE_PRISONER_BAIL`), so 49 is `DA_CLEAR_BOUNTY`; arrest-related: 6 ATTACK_CHASE_FOREVER, 8 TAKE_MONEY, 10 PAY_BOUNTY, 29 ARREST_TARGET, 30 ARREST_TARGETS_CARRIED_PERSON, 32 ASSIGN_BOUNTY (matched to the action switch `FUN_140680560`, **Observed**). DC compare-by: 0 `==`, 1 `<`, 2 `>` | Full lists in [dialogue](dialogue.md#enums). From the FCS exe decompile (`probes\characters-fcs-decomp`), enum names and values only |

### Signal functions and the package classes

**Verified** (factory `FUN_140285ba0` builds the subclass from the signal value with one switch case per value; each constructor's
vtable is the RTTI class named here; the editor's enum names are matched to the classes by meaning).

| Signal | FCS name | Package class | Signal | FCS name | Package class |
|---|---|---|---|---|---|
| 1 | SIG_RETREAT_CANNIBAL_RAID | CannibalRetreatFromRaid | 20 | WAR_ASSAULT_TOWN | Package_WarAssault |
| 4 | SIG_CANNIBAL_START_PATROL | GoOutOnPatrol_Cannibal | 22 | SIG_REFUGEE_DISBAND | Package_Refugee |
| 5 | SIG_SQUAD_NOT_INTACT | RetreatAndRefresh | 23 | SIG_START_PATROL_RUNNING | GoOutOnPatrol_Running |
| 6 | SIG_START_PATROL | GoOutOnPatrol | 24 | SIG_GATHER_UP_ALL_LOCAL_PRISONERS_UNHOLY | PrisonerGathering_unholy |
| 10 | SIG_GATHER_UP_ALL_LOCAL_PRISONERS | PrisonerGathering | 25 | SIG_RETREAT_CANNIBAL_RAID_TO_NEAREST_NEST | CannibalRetreatFromRaid_nearestNest |
| 11 | SIG_SLAVER_SHIP_TO_WORKCAMPS | SlaverPrisonerShipping_Camps | 27 | SIG_CANNIBALS_AT_HOME | Package_CannibalsAtHome |
| 12 | SIG_BODYGUARD | Contract_HiredAlly | 28 | SIG_SLAVER_SHIP_TO_ANYWHERE_CLOSE | SlaverPrisonerShipping_AnywhereTownsIncluded |
| 13 | SIG_BECOME_EX_SLAVE | Contract_UntilExSlave | 29 | SIG_PASSING_BY_TOWN_ASSAULT | Package_PassingByTownAssault |
| 14 | SIG_OUT_OF_DANGER | Contract_UntilOutOfDanger | 30 | SIG_OCCUPY_CONQUERED_TOWN | Package_OccupyTown |
| 15 | SIG_DEFEND_PLAYER_TOWN_TIMED | Contract_DefendPlayerBase | 31 | SIG_DEFEND_PLAYER_TOWN_CAMPAIGN | Campaign_DefendPlayerBase |
| 16 | SIG_WANDERING_TOWN_TO_TOWN | Package_WanderingTrader | 32 | WAR_ASSAULT_TOWN_CANNIBAL | Package_WarAssault_Cannibal |
| 17, 26 | SIG_HANG_OUT_AT_BAR, SIG_HANG_OUT_OUTDOORS_ONLY | Package_HangAroundBar (constructor argument 0 / 1) | 33 | SIG_TIMED_CONTRACT | Contract_Timed |
| 19 | WAR_GATHER_FORCES | Package_WarBase | 0, 2, 3, 7, 8, 9, 18, 21 | SIGNAL_NONE, SIG_AT_SAFE_TOWN, SIG_AT_HOME_TOWN, SIG_DIPLOMAT_MISSION, SIG_DEFEAT_SQUAD, SIG_HOME_OWNER_KEEP_LOCKED, SIG_RAIDING_WEAK_VILLAGES, WAR_BATTLE_MEETING | plain `AIPackage` |

### Package classes

**Verified** by code (the blackboard update `FUN_1402732b0` → slot walk `FUN_140272570`, activation `FUN_1402721a0`, the class vtables) unless marked.
- Vtable: +0x30 start (called on activation after the old package's +0x38 end), +0x48 update (the running package, after the choice), +0x50 sees a character,
  +0x60 can start, +0x68 finished, +0x90 may hand over to `Leads to` (base: yes; a finished package whose `Leads to` is refused fails its slot), +0x18 speed
  mode (base 4, PassingByTownAssault 2, Refugee and DefendPlayerBase 1). Package fields: +0x8 `mission town`, +0x28 `mission target`, +0x48 `mission employer` (saved under these keys).
- Blackboard current town (+0x138, `FUN_1402688d0`, before the choice): the leader's town, else the nearest town (any type but nest markers) whose radius
  (`FUN_140926d50` × 1) holds the platoon; entering one sets the arrival hour (+0x140) and fires the leader's ENTER_TOWN (49).
- **Package_HangAroundBar** (17/26): can start in its home town, or in a town not hostile to it that has bars (`vfunc 0x2d0` kind 3); finished in a player town
  that is not `is public` (the squad not the player's) or when it can no longer start. So a bar package runs where the squad is.
- **Package_WanderingTrader** (16): start draws the stay W = uniform[5, 8) h, state 0 and the next town with the chooser `FUN_14092c5e0` (faction = own, from the
  platoon's position, excluding the current town, mask 0x6c: towns of type town, village, slave camp or military, or any player town; kept only when `is public`
  (Town +0x380) or of its own faction, not hostile, not in its `no-go zones` (FactionWarMgr +0xa0); the 4 nearest by X/Z, one uniformly). Update: no town → start
  again; in its town state 0 → 1 → 2, and when ready (`FUN_1402868c0`: in a player town that is not public at once, else W h after arriving and between
  06:00 and 20:59) it drops its town (`FUN_140288bd0`) to draw another. Can start while state < 2 or when ready; finished at state 2. So templates with a slot-0 bar
  package hop town to town, sitting in each town's bar 5-8 h. Unread: a leader flag (Character +0x648 → +0x70) that also lets it leave 0.2 h after being set (**Unknown**).
- **Package_PassingByTownAssault** (29): can start when `attacks` (settings.cfg) is not 5, it is not spent (+0xb4), its leader's town is a player town whose faction it is
  not allied with (`FUN_1406b2630`: same or relation ≥ 50), the platoon is intact, the faction's wait has passed (global map by faction `DAT_14212dde0` of
  {last end, running}: free while one runs; a faction that never ended one is free; else more than the wait since the last end) and that town's faction has someone
  within 2500 of it (and Town vfunc 0x2f0, +0x3b0 set, **Unknown**). Start: the wait for next time by `attacks` (`FUN_140286a70`, uniform, game hours **Observed**: 0 none, 1 [6, 48),
  2 [32, 96), 3 [72, 144), 4 [130, 188)), the town as mission town and target, its faction the enemy (+0x100), start hour, and the enemy's people within 2500 as targets;
  each one seen later is added and tagged squad memory 4. End: the faction's running count drops, at 0 the last-end hour is set. Finished: not intact (spent); else not
  before 0.25 h, not while a target stands (alive, up, in the town; the town-compare polarity is **Observed**), then (spent) with at least 2 members up. May lead
  (to the occupation): intact, no target standing, at least 2 up. Data (rebirth.mod): 33 bandit/raider templates have it at slot 1-3 with `hostile occupy town` above it.
- **Package_OccupyTown** (30): never starts on its own (only by `Leads to`). Start: the leader's town as mission town, its faction the enemy (+0xb8, memory 4 on sight),
  each member fires TAKEN_OVER_PLAYER_TOWN (73), the platoon made persistent if it was not (undone at the end). Update: the platoon joins the town's occupier set
  (Town +0x118; its count +0x128 is "Hostile Occupiers:" in the town panel, `FUN_140922e60`). End: removed from the set, "{1} squad has been removed from {2}"
  (faction, town). Finished: the town gone (vfunc 0x2f0 false), or no member alive and up in it (`FUN_140289180`) for more than 4 of its timer (`DAT_142133794` per
  call, read as seconds, **Observed**). No ownership change: the town stays the player's. `campaign victory occupy town` sits only in an unread `victory AI leader`.
- **Package_Refugee** (22, 4 templates): can start when its home town's vfunc 0x288 (TownBase +0xe0 → +8, **Unknown**, read as destroyed) or a squad of fewer than 3 with
  `FUN_1405c8aa0` > 0; start: not persistent, flag "broken" when not intact, mission town = the own faction's nearest town other than home (`FUN_140927ad0`), re-homed
  there when home is gone; update: a broken one merges into the target squad within 1000 and disbands; finished when the leader is in a town (**Observed**). Not built.
- **Campaign_DefendPlayerBase** (31): start: the player town nearest its platoon (`FUN_140927f10`) as mission town; finished when its employer turned on it (+0xa8) or the
  blackboard's contract time (+0x218) has passed; sees the town faction as allies (memory 3); its end posts "Contract with {faction} has ended." / "has been
  terminated." when its employer is the player's. Only as a campaign's `pt2` package (the campaign drives it).
- **Package_WarBase** (19) / **Package_WarAssault** (20, 32): finished when the platoon is out of its campaign; no update of their own (the campaign drives them,
  [campaigns](factions-squads-towns.md#campaigns)).

### Goal loading

1. A package is activated (`FUN_1402721a0`), which clears the Blackboard's role lists 0, 1, 2 and 4 (list 3 is not cleared), marks the
   package as just activated, then calls the goal loader (`FUN_140270850`).
2. `inherits from` is processed first, recursively, so a parent's entries are added first. Adding goes through `FUN_140268e10`, which
   calls `FUN_14027b3a0`; that skips an entry whose task type and target hand equal one already in the list, so **the parent's
   entry wins**.
   One base-game record has two parents (`warrior guild`: `Guard building, residents` and `Shop-24hr`, which share an ancestor; **Observed** in the data 2026-10-14); Meitou
   loads them in the listed order and counts only a true cycle as a problem. Whether the original walks every parent is **Unknown**.
3. Entries go into five role lists on the Blackboard (index to offset is identical in `FUN_140268e10` and the sync `FUN_14026b790`,
   and the constructor lays the lists out at that stride):

| Index | Role | Blackboard offset | Filled from |
|---|---|---|---|
| 0 | Squad | +0x1b0 | `Squad AI Goals`, else `Leader AI Goals` |
| 1 | Squad2 | +0x1c8 | `Squad2 AI Goals`, else Squad, else Leader |
| 2 | Leader | +0x198 | `Leader AI Goals` |
| 3 | (unused) | +0x1e0 | never filled by this loader |
| 4 | Slave | +0x1f8 | `Slave AI Goals` only |

## Unknowns

- Which of the six category registries (flag bits 1, 2, 4, 0x10, 0x20, 0x40; [planner](ai-tasks.md#planner)) a character plans with; the meaning of the `*` marker on some
  condition entries (the entry's second flag byte) and of the TaskData flag +0x14.
- The extra x1.25 under an owner-object condition in scoring; the rule that drops a candidate whose B list holds with no effect.
- The "AI flag +0xbc" that lets kind-2 (turret) perma-jobs run before urgent tasks; +0x70 of `OrdersReceiver` is the one-shot order list ([perma-jobs](ai-tasks.md#perma-jobs)).
- Why `clears existing jobs` has no string reference in the exe (editor-only field, or read some other way).
- Which AI packages use which `unloaded func` value (not surveyed in the data). The unloaded update itself, the keep test `FUN_1407ec570`
  and the 60 s orphan timer are in [off-screen world](factions-squads-towns.md#off-screen-world).
- The meaning of `squad mem type` value 3, and Blackboard role list 3. Tiers 0 (JUST_ACTION) and 4 (OBEDIENCE) are never scored by the
  chooser: the normal passes sync role lists with the mask `1 << tier` for tiers 3, 2, 1, and the slave special case in `FUN_14050e5b0`
  syncs role list 4 with mask 0xe (tiers 1..3); what tier 0 and 4 are for is **Unknown**.
- Why 37 task types have no TaskData; types 289 and 290 (no enum name).
- `ending` events (DA/DC behaviour: [dialogue](dialogue.md#unknowns)).
- What squad-template slots 1..4 mean beyond descending priority.
