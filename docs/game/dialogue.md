# Dialogue

How the original picks, says and acts on dialogue. Sources: the FCS editor's enums (decompile, outside the repo), the install's
`fcs.def` / `fcs_enums.def` / `data/gui/layout`, the kenshi_x64.exe decompile (functions cited), and a probe over the merged base
load order (`gamedata.base`, `Newwworld.mod`, `Dialogue.mod`, `rebirth.mod`; 2026-10-17). Claims are **Observed** (decompile reading)
unless marked. Engine: [Meitou design](#meitou-design).

## Data

**Verified** by probe: 293 DIALOGUE_PACKAGE, 1890 DIALOGUE, 22783 DIALOGUE_LINE, 21135 DIALOG_ACTION, 289 WORD_SWAPS records.

### Who has which dialogue

`FUN_14062b210` (character setup) and `FUN_1407ff8b0` (player characters):

| Character | Packages |
|---|---|
| any | CHARACTER `dialogue` (161 uses): each DIALOGUE goes straight to event 1 (fcs.def says the same, **Verified** text) |
| NPC squad leader (member 0) | SQUAD_TEMPLATE `dialog leader` (157), else CHARACTER `dialogue package` (793), else `dialog squad` (**Observed**, decompile) |
| other NPC members (and a leader with neither) | SQUAD_TEMPLATE `dialog squad` (216), else CHARACTER `dialogue package` |
| animal | SQUAD_TEMPLATE `dialog animal` (4) |
| player character, not an animal | CHARACTER `dialogue package player` (179), else the player faction's FACTION `dialog default` (25 records have one), else the package `5369-gamedata.base` ("player character default", **Verified** by probe) |

No NPC faction or race package is read. Other users of DIALOGUE (**Verified** counts): BIOME_GROUP `arrival dialog` 194,
FACTION_CAMPAIGN `dialog announcement` 121, AI_PACKAGE `contract end talk passive` 26, SINGLE_DIPLOMATIC_ASSAULT 13.

### DIALOGUE_PACKAGE

- `inheritsFrom` (457 refs) merges first, recursively, a visited set stopping loops (`FUN_14067fab0`).
- `dialogs`: each of the reference's val0, val1, val2 that is non-zero and not 100 is an `EventTriggerEnum` the dialogue answers
  (**Verified** by probe: 2586 refs, 127 with val1 and 30 with val2 set, all valid event numbers; 17 have val0 0).
- A package or DIALOGUE whose name contains `Unfinished` is skipped (`FUN_14067e220`; 9 packages, 24 dialogues).

### DIALOGUE and DIALOGUE_LINE

One shared runtime object per DIALOGUE / DIALOGUE_LINE / WORD_SWAPS record (`FUN_14067ddd0`, reader `FUN_14067b070`, tree
`FUN_14067d260`), used by every character. A DIALOGUE is a root; `lines` are DIALOGUE_LINE children, recursively; the tree may
link back to earlier lines (loops; **Verified** in the data).

| Field | Meaning |
|---|---|
| `speaker` (`TalkerEnum`) | who says the node ([Speakers](#speakers)). Lines: T_ME 15365, T_TARGET 4976, T_TARGET_IF_PLAYER 329, interjectors 1471/485/145, whole squad 7, with race 5 (**Verified**) |
| `text0`..`textN` | alternatives (up to 43 in the data); the non-empty ones are kept and one is picked uniformly each time the line is said (`FUN_140675320`) |
| `repetition limit` (`DialogRepetitionEnum`) | cooldown hours: 0 none, 1 → 2, 2 → 6, 3 → 48, 4 → 168, 5 once only (5000), 6 → 0.33 |
| `chance permanent`, `chance temporary` | percent; below 99 the line rolls ([Line check](#line-check)) |
| `target is type` (`CharacterTypeEnum`) | the target's NPC class must match |
| `score bonus` | added to the [score](#score) |
| `locked`, `one at a time`, `monologue` | DIALOGUE only. `monologue` is set on 1888 of 1890 roots (**Verified**), including event-1 talk dialogues, so it does not mean "no player replies" |
| `interjection` | interjection node ([Speakers](#speakers)) |
| `conditions` | DIALOG_ACTION {`condition name` (DC), `compare by`, `who`}, value = the reference's val0 |
| `effects` | DIALOG_ACTION {`action name` (DA)}, value = val0; DA_NONE entries are skipped |
| `in town of`, `my faction`, `target faction`, `my race`, `my subrace`, `target race`, `is character`, `has package`, `target carrying character`, `target has item`, `target has item type`, `world state` | extra checks ([Line check](#line-check)) |
| `give item` (val0 = count), `change relations` (val0), `unlocks`, `unlock but keep me`, `locks`, `interrupt`, `crowd trigger`, `trigger campaign` (val0..val1 hours, val2 %), `change AI`, `AI contract` (val0 hours) | effects ([Effects](#effects)) |

Never read by the exe (strings absent): DIALOGUE `for enemies`, `no target race`, CHARACTER `announcement dialogue`.

### WORD_SWAPS

A record named after a token (`OUTLANDER`, `RACENAME`, ...; 289) whose `lines` are DIALOGUE_LINEs with texts and conditions. See
[Tokens](#tokens).

## Enums

**Verified**: the FCS editor's enums, value = position. `fcs_enums.def` has only `EventTriggerEnum`, as a commented subset in another
order (no EV_NONE, the two blank names, EV_ESCAPING_SLAVE_SPOTTED, EV_MAX), so the editor's numbering is authoritative.

- **EventTriggerEnum**: 0 NONE, 1 PLAYER_TALK_TO_ME, 2 ANNOUNCEMENT, 3 I_SEE_NEUTRAL_SQUAD, 4 I_SEE_RAGDOLL, 5 `EV_______`,
  6 SOUND_THE_ALARM, 7 `EV_I_________`, 8 THIEF_CAUGHT_STEALING_FROM_ME, 9 SHOO_FROM_MY_BUILDING, 10 MARKED_FOR_DEATH,
  11 SCREAMING_TORTURE, 12 BAR_TALK, 13 UNLOCK_MY_CAGE_OR_SHACKLES, 14 UNLOCK_MY_CAGE_ATTEMPT, 15 I_DEFEATED_SQUAD, 16 LAUNCH_ATTACK,
  17 INTRUDER_FOUND, 18 HEALING_OTHER_START, 19 BEING_HEALED_START, 20 HEALING_OTHER_FINISHED, 21 BEING_HEALED_FINISHED,
  22 FIRSTAID_KIT_EMPTY, 23 GET_UP_PEACE, 24 GET_UP_FIGHT, 25 GET_UP_UNNECCESSARY_FIGHT, 26 HARRASSMENT_SHOUTS, 27 I_SEE_ANIMAL_SQUAD,
  28 SPEECH_INTERRUPTED_ATTACKED_BY_TARGET, 29 SPEECH_INTERRUPTED_ATTACKED_BY_STRANGERS, 30 CONTRACT_JOB_ENDED, 31 BETRAYAL,
  32 LOOTING_WEAPON_ONLY, 33 LOOTING_EVERYTHING, 34 I_SEE_UNIFORM_IMPOSTER, 35 INTRODUCING_NEW_SLAVE, 36 ESCAPING_SLAVE_SPOTTED,
  37 RECAPTURED_A_SLAVE, 38 SHOUT_AT_SLAVE_WORKER, 39 SLAVE_DELIVERY, 40 ESCAPED_EX_SLAVE_SPOTTED, 41 WITNESS_GENERIC_ASSAULT,
  42 WITNESS_LOOTING_ALLY, 43 WITNESS_THIEF_OR_LOCKPICK, 44 BOUNTY_SPOTTED, 45 ESCAPED_PRISONER_SPOTTED, 46 PRISONER_FREE_TO_GO,
  47 ALMOST_WOKE_UP, 48 ENTER_BIOME, 49 ENTER_TOWN, 50 SQUAD_BROKEN, 51 BOUGHT_ME_FROM_SLAVERY, 52 EATING_SOMETHING_SOUNDS,
  53 WORSHIPING_SOMETHING, 54 SLAVE_ESCAPE_OPPORTUNITY_SAVIOR, 55 SLAVE_ESCAPE_OPPORTUNITY_ALONE, 56 ASSASSINATION_FAILED,
  57 EATING_MY_CROPS, 58 KIDNAPPING_MY_ALLY, 59 USING_MY_TRAINING_EQUIPMENT, 60 GIVE_UP_CHASE, 61 ACID_FEET, 62 ACID_RAIN,
  63 ACID_WATER, 64 WINDY, 65 POISON_GAS, 66 I_SEE_ENEMY_PLAYER, 67 I_SEE_ALLY_PLAYER, 68 I_SEE_ILLEGAL_PLAYER_BUILDING, 69 BURNING,
  70 LOST_LEG, 71 LOST_ARM, 72 I_SEE_PLAYER_NICE_BUILDING, 73 TAKEN_OVER_PLAYER_TOWN, 74 CROWD_TRIGGERED, 75 MAX (all `EV_`-prefixed).
- **DialogActionEnum**: 0 DA_NONE, 1 TRADE, 2 TALK_TO_LEADER, 3 JOIN_SQUAD_WITH_EDIT, 4 AFFECT_RELATIONS, 5 AFFECT_REPUTATION,
  6 ATTACK_CHASE_FOREVER, 7 GO_HOME, 8 TAKE_MONEY, 9 GIVE_MONEY, 10 PAY_BOUNTY, 11 CHARACTER_EDITOR, 12 FORCE_SPEECH_TIMER,
  13 DECLARE_WAR, 14 END_WAR, 15 CLEAR_AI, 16 FOLLOW_WHILE_TALKING, 17 THUG_HUNTER, 18 JOIN_SQUAD_FAST, 19 REMEMBER_CHARACTER,
  20 FLAG_TEMP_ALLY, 21 FLAG_TEMP_ENEMY, 22 MATES_KILL_ME, 23 MAKE_TARGET_RUN_FASTER, 24 GIVE_TARGET_MY_SLAVES, 25 TAG_ESCAPED_SLAVE,
  26 FREE_TARGET_SLAVE, 27 MERGE_WITH_SIMILAR_SQUADS, 28 SEPARATE_TO_MY_OWN_SQUAD, 29 ARREST_TARGET, 30 ARREST_TARGETS_CARRIED_PERSON,
  31 ATTACK_TOWN, 32 ASSIGN_BOUNTY, 33 CRIME_ALARM, 34 RUN_AWAY, 35 INCREASE_FACTION_RANK, 36 LOCK_THIS_DIALOG, 37 ASSAULT_PHASE,
  38 RETREAT_PHASE, 39 VICTORY_PHASE, 40 ENSLAVE_TARGETS_CARRIED_PERSON (all `DA_`-prefixed), 41 CHOOSE_SLAVES_SELLING,
  42 CHOOSE_SLAVES_BUYING, 43 CHOOSE_PRISONER_BAIL, 44 CHOOSE_CONSCRIPTION, 45 CHOOSE_RECRUITING, 46 CHOOSE_HIRING_CONTRACT,
  47 SURRENDER_NON_HUMANS, 48 CHOOSE_ANIMALS_BUYING, 49 DA_CLEAR_BOUNTY, 50 DA_PLAYER_SELL_PRISONERS,
  51 DA_PLAYER_SURRENDER_MEMBER_DIFFERENT_RACE, 52 DA_SUMMON_MY_SQUAD, 53 DA_REMOVE_SLAVE_STATUS, 54 DA_OPEN_NEAREST_GATE,
  55 DA_ATTACK_STAY_NEAR_HOME, 56 DA_MASSIVE_ALARM, 57 DA_ATTACK_IF_NO_COEXIST, 58 DA_KNOCKOUT, 59 DA_END.
- **DialogConditionEnum**: 0 DC_NONE, 1 RELATIONS, 2 PLAYERMONEY, 3 REPUTATION, 4 CARRYING_BOUNTY_ALIVE, 5 CARRYING_BOUNTY_DEAD,
  6 FACTION_VARIABLE, 7 IMPRISONED_BY_TARGET, 8 IMPRISONED_BY_OTHER, 9 IS_A_TRADER, 10 FACTION_RANK, 11 BUILDING_IS_CLOSED_AND_SECURED,
  12 PLAYER_TECH_LEVEL, 13 NUM_DIALOG_EVENT_REPEATS, 14 IS_IMPRISONED, 15 IMPRISONMENT_IS_DEATHROW, 16 TARGET_IN_TALKING_RANGE,
  17 IN_MY_BUILDING, 18 TARGET_LAST_SEEN_X_HOURS_AGO, 19 IS_LEADER, 20 MET_TARGET_BEFORE, 21 WEAKER_THAN_ME, 22 STRONGER_THAN_ME,
  23 HAS_TAG, 24 IS_ALLY, 25 IS_ENEMY, 26 PERSONALITY_TAG, 27 BROKEN_LEG, 28 BROKEN_ARM, 29 DAMAGED_HEAD, 30 NEARLY_KO,
  31 IN_A_NON_PLAYER_TOWN, 32 IS_RUNNING, 33 COPS_AROUND, 34 `NULL_NULL_____DC_TARGET_SQUAD_SIZE`, 35 SQUAD_SIZE, 36 IS_PLAYER,
  37 NUM_BACKPACKS, 38 SQUAD_ONLY_ANIMALS, 39 IS_OUTNUMBERED, 40 BOUNTY_AMOUNT_PERCEIVED, 41 IS_KO, 42 IS_NEARLY_KO, 43 SQUAD_IS_DOWN,
  44 IS_DEAD, 45 IS_FEMALE, 46 CARRYING_SOMEONE_TO_ENSLAVE, 47 BOUNTY_AMOUNT_ACTUAL, 48 IM_UNARMED, 49 TOWN_HAS_FORTIFICATIONS_WALLS,
  50 TARGET_IS_MY_MISSION_TARGET, 51 MY_MISSION_IS_FRIENDLY, 52 I_LOVE_THIS_GUY, 53 I_HATE_THIS_GUY, 54 I_SHOULD_SCREW_THIS_GUY_OVER,
  55 I_SHOULD_HELP_THIS_GUY, 56 IN_COMBAT, 57 WITHIN_TOWN_WALLS, 58 TOWN_WALLS_LOCKED_UP, 59 IS_SLAVE, 60 HAS_A_BASE_NEARBY,
  61 TARGET_IS_SLAVE_OF_MY_FACTION, 62 IS_ESCAPED_SLAVE, 63 IS_IN_LOCKED_CAGE, 64 WEARING_LOCKED_SHACKLES, 65 IS_SAME_RACE_AS_ME,
  66 CAN_AFFORD_BOUNTY, 67 IS_SNEAKING, 68 IS_INDOORS, 69 HAS_ILLEGAL_ITEM, 70 USING_MY_TRAINING_EQUIPMENT, 71 STARVING,
  72 MIXED_GENDER_GROUP, 73 TOWN_LEVEL_CURRENT_LOCATION, 74 PLAYERS_BEST_TOWN_LEVEL, 75 IN_A_PLAYER_TOWN, 76 TARGET_CHARACTER_EXISTS,
  77 IS_RECRUITABLE, 78 HAS_AI_CONTRACT, 79 HAS_ROBOT_LIMBS, 80 DC_END (all `DC_`-prefixed).
- **DialogRepetitionEnum**: 0 DR_NO_LIMIT, 1 DR_SHORT_2, 2 DR_MEDIUM_6, 3 DR_LONG_48, 4 DR_LONG_1WEEK, 5 DR_ONCE_ONLY, 6 DR_VSHORT_020.
- **TalkerEnum**: 0 T_ME, 1 T_TARGET, 2 T_TARGET_IF_PLAYER, 3 T_INTERJECTOR1, 4 T_INTERJECTOR2, 5 T_INTERJECTOR3, 6 T_WHOLE_SQUAD,
  7 T_TARGET_WITH_RACE.
- **CharacterTypeEnum**: 0 OT_NONE, 1 OT_LAW_ENFORCEMENT, 2 OT_MILITARY, 3 OT_TRADER, 4 OT_CIVILIAN, 5 OT_DIPLOMAT, 6 OT_SLAVE,
  7 OT_SLAVER, 8 OT_BANDIT, 9 OT_ADVENTURER, 10 OT_END.
- **PersonalityTags** (DC 26 `tag`): 0 PT_NONE, 1 HONORABLE, 2 TRAITOROUS, 3 `PT___`, 4 `PT__`, 5 SMART, 6 DUMB, 7 `PT____`,
  8 `PT_____`, 9 BRAVE, 10 FEARFUL, 11 WARM_KIND, 12 COLD_CRUEL, 13 NORMAL, 14 MANIACAL, 15 PT_END.
- **CharacterPerceptionTags_LongTerm** (DC 23 `tag`): 0 LT_NONE, 1 MY_INTRUDER, 2 MY_LIFESAVER, 3 FREED_ME, 4 STOLE_FROM_ME,
  5 MY_CAPTOR, 6 FRIENDLY_AQUAINTANCE, 7 DEFEATED_MY_SQUAD_ONCE, 8 SQUAD_LOST_TO_ME_ONCE, 9 KILLED_MY_FRIEND, 10 I_SCREWED_THIS_GUY,
  11 LT_MAX.

**Use in the data** (**Verified** by probe, references counted).
- Events (`dialogs` val0): 3 ×294, 1 ×220, 13 ×147, 15 ×87, 24 ×82, 10 ×79, 18 ×79, 8 ×69, 11 ×68, 21 ×63, 42 ×63, 19 ×62, 14 ×61,
  29 ×59, 66 ×59, 9 ×57, 28 ×56, 67 ×55, 22 ×53, 43 ×53; every other event fewer than 50.
- Effects: DA 6 ×1192, 33 ×417, 16 ×415, 12 ×389, 8 ×352, 36 ×244, 34 ×186, 29 ×176, 37 ×160, 4 ×154, 3 ×138, 7 ×128, 20 ×115,
  18 ×112, 0 ×102, 28 ×85, 1 ×80, 38 ×79, 2 ×72, 32 ×64, 21 ×63, 9 ×44; the rest fewer than 35.
- Conditions: DC 26 ×2125, 56 ×958, 14 ×892, 2 ×537, 35 ×417, 36 ×395, 17 ×367, 59 ×362, 16 ×357, 45 ×315, 65 ×310, 23 ×276, 32 ×243,
  31 ×237, 1 ×189, 27 ×188, 43 ×177, 38 ×167, 28 ×162, 41 ×158, 24 ×134, 77 ×133, 19 ×122, 18 ×117, 20 ×116, 79 ×103; the rest
  fewer than 100. `who`: 0 ×5311, 1 ×5550, 2 ×70, 3 ×7.

## Runtime

Each character has one `Dialogue` state (Character+0x280): owner, target, current event, root and current line, interjectors 1-3,
bubble and timers, choice list, per-event cooldowns and last target, per-event repetition counter, lock map, permanent-chance map,
package set.

### Firing an event

`FUN_140684990(dialogue, target, event)` refuses when the event's cooldown is above 0, the owner is dead, KO or asleep (unless the
event is in a mask `DAT_141eeaecc`, contents **Unknown**), the owner is talking already (unless the event is one of 2, 6, 11, 13,
17, 26, 30, 31, 34, 36, 40, 45, 46, 48, 51, 54, 74), or an owner-state test fails (skipped for 6, 11, 39). After a successful 3, 4,
27, 66 or 67 that event has a 1.0 cooldown (frame dt).

Event 1 starts a [player conversation](#player-conversation), using a stored `interrupt` DIALOGUE instead of the event-1 list when one
is pending. Every other event starts an [ambient conversation](#ambient-conversations).

Sources (call sites): 1 the player's Talk order (task 12, after reaching the NPC; also tasks 102/159/161 when the package's event is
0); 3, 27, 34, 59, 66, 67 perception (`FUN_140859680`); 10 `FUN_1408d0d90`; 11 torture; 15 `CharacterAnimal::vfunc_62`; 18-21 first
aid (start: the medic 18 at the patient, the patient 19 back unless down; finish: 20 / 21 likewise; none when treating oneself);
22 empty kit (`Task_FirstAidRig`, the medic at the patient); 23-25 getting up (`Task_GetUp`, no target: 24, or 23 when the character's two
enemy counters (+0x94, +0x98) sum below 1; a variant `FUN_1408c6780` fires 25 on further counters, **Unknown** which); 26 harassment; 30 contract end (target = employer); 32/33 AI looting; 37 recapture; 47 almost
woke; 49 entering a town (no target); 50 squad broken; 51 slave bought; 52 eating a prisoner; 53 worship; 60 giving up a chase
(move, follow, attack, talk tasks); 61-63 acid; 66 also from DA 56; 68 illegal player building; 73 town taken over; others from
lock and prison code: 13 from `Task_PickLock`, `Task_CutShackles` and `Task_BruteForceShackles` (the freed character at the
worker, when they differ); 31 `Contract_HiredAlly::vfunc_8` (betrayal); 36 the witness function and perception; 44 `FUN_140856f00`;
70 / 71 losing a leg (limb slots 2, 3) / an arm (`FUN_14064edc0`, no target).

**Witnesses** (`FUN_140854d10`, **Observed**): a witness reacting to a crime fires an event at the criminal by the crime code
(economy.md 11.2): Burglary 2 and Terrorism 7 → 43; Theft 3, Looting 10, Fencing 13 → 42, or 8 when the victim is in the witness's
platoon; Trespassing 11 → 17 (also fired by perception when it sets the crime, economy.md 11.2); Crop theft 14 → 57; Enslaving 1, Kidnapping 15 → 58; Uniform theft 16 → 34; the rest 41. Each case has
further tests that return without firing (partly read).

### Choosing a line

`FUN_14067a0c0`. Candidate lists are sorted by [score](#score), highest first, without duplicates; the walk stops at the first
entry scoring below the best so far (roots excepted). For each node the [speaker](#speakers) is resolved and the
[line check](#line-check) runs with that speaker as "me"; a passing root descends into its children (root score + child score), a
passing leaf is a candidate. **One candidate at the top score is picked uniformly.** An interjection node first finds and stores an
interjector, then descends.

### Score

Computed at load: +1 speaker not T_ME, + `score bonus`, +2 `locked`, +2 for each non-empty `in town of` / `my faction` /
`target faction` / `target race` / `target has item type`, +2 each `target has item` and each `world state` entry, +1 each condition
(+1 more for a tag condition), +1 `chance permanent` below 99, +1 `target is type` above 0, +2 interjection node, +1 speaker 3-6. At
choice time +2 when the target's race is in `target race` (`FUN_140671bb0`). A root's effective score is its own plus its best
child's (`FUN_1406ad9d0`).

### Line check

`FUN_140678f30`, all must pass, in order:
1. Tag masks (DC 23 / 26 are folded at load into required and forbidden bitmasks by the DIALOG_ACTION's `tag`, val0 1 = required).
   PERSONALITY_TAG tests the speaker's personality; HAS_TAG tests the speaker squad's memory flags about the target (all required
   set, no forbidden).
2. A squad test for flagged lines and a per-character line flag (purposes **Unknown**).
3. `unique` (no current field sets it).
4. `chance temporary` below 99: a fresh 0-100 roll every check.
5. `in town of` (the speaker's current town's faction), `my faction`, `target faction`, `has package` (any of the speaker's
   packages), `is character` (the speaker's record), `target carrying character` (someone in the target's squad carries a listed
   character; recorded as interjector), `world state` (all equal their bool), `target has item type` / `target has item` (the target's
   squad members' inventories and backpacks), `target race` (the target, else a squad member, recorded as interjector), `my race`,
   `my subrace`. Each list is OR within itself.
6. `repetition limit`: fails while now − last use is below the cooldown.
7. `target is type`: the target's NPC class (OT_SLAVE also matches a target in slave state 1/2 and then **skips steps 8-9**).
8. `chance permanent` below 99: a roll made once per character and line, then cached.
9. The DC list in order ([Conditions](#conditions)).

### Speakers

`FUN_140672bf0`: T_ME and T_WHOLE_SQUAD the owner (how WHOLE_SQUAD fans out is **Unknown**); T_TARGET the target; T_TARGET_IF_PLAYER
the target if it is a player-faction character, else the line is skipped; T_INTERJECTOR1-3 the stored interjectors; T_TARGET_WITH_RACE
the first member of the target's squad whose race is in `target race`.

Interjectors (`FUN_140679bc0`): members of the **owner's own squad**, awake, not the owner, not an animal, not talking, not already an
interjector, passing the node's check as speaker. A `dumb` character qualifies only when the node's `is character` lists it (fcs.def
`dumb` "prevents them becoming interjectors or talking to NPCs", **Verified** text). Weight = clamp((d² − 202800) / (900 − 202800), 0,
1): 1 within 30 units, 0 beyond about 450; ×0.1 for a different race (inferred). The weighted pick rule is **Unknown**.

### Player conversation

`FUN_140684320`:
- Dropped unless the player character is within 9216 units of the camera node (inferred as the camera).
- Plays Wwise `Initiate_Conversation`. Chooses the root and its best child, says it.
- Opens the DialogueWindow when the said line has passing children; **the window pauses the game** (clock speed 0, the previous speed
  restored on close, `FUN_140787fb0`).
- The talking player character is the one given the Talk order; walking there is that task's precondition. AI-to-AI talk range is
  260 units (DC 16).

**Saying a line** (`FUN_1406839a0`): in a player conversation a line with **no children ends the conversation at once** without being
shown (whether the original shows such closing lines elsewhere is **Unknown**). Otherwise: pick a text and mark the line and all its
parents used, expand [tokens](#tokens), **run its effects at once**, log the text and put it in the NPC panel, then build the choices.

**Choices** (`FUN_140680130`, `FUN_140683c00`): the said line's children are checked in list order (me = NPC, target = the player
character). If a passing child has speaker T_ME, a single `[...]` button continues to it and the scan stops; otherwise every passing
child is a button with a random text. Clicking one logs "<name>: <text>", plays `Dialogue_Choice` / `Continue_Conversation`, marks the
reply used, **runs its effects**, then chooses and says the reply's best child.

### Ambient conversations

`FUN_140683f90(dialogue, target, line, event, force)`: a monologue root outside event 1 shows a **speech bubble**, no window; a root
says its best child (as in [Choosing a line](#choosing-a-line)), any other line is said as given. Large font for events 2, 11, 17, 26,
31, 34, 36, 40, 45, 74, medium for the interrupting set.

**Chain** (`FUN_140684720`, **Observed**): when a bubble ends, the owner's state picks the best child of the said line with full
speaker resolution (owner, target, interjector), copies its target to that speaker's state, and that speaker says it; a line said by
someone else hands back to the owner's state, so the tree stays rooted at the owner. This is how two NPCs talk: the target says the
T_TARGET lines. A non-monologue root in bubble mode ends after its first line (root flag `+0x1e1`, `FUN_140685090`). **Verified** (base
data probe): every root used for events 2, 3, 13, 18 and 66, every campaign announcement and every crowd trigger is a monologue.

**Sight** (`FUN_140859680`, **Observed**, partly read): event 3 fires for a seen character outside the player's faction too (an NPC of
another squad); 66 and 67 need a player-faction target.

**Timing** (`FUN_140685090`): a bubble lasts 2.0 + 0.12 × text length in dt units (presumably seconds); DA 12 sets a second timer
(val0). Both run only while unpaused; the next line starts when both are below 0.

**End** (`FUN_140674830`): bubble and window closed, the per-event counter +1 with its time (read by DC 13), the event's last target
stored, the squad blackboard notified for player conversations.

**AI tasks 102/159/161** (`Task_TalktoPlayer`): fire the AI package's event (0 means 1) at the target; event 9 repeats every update
(17 while the target is in my building); gives up after 25 game minutes unreachable with event 60; done when the Dialogue is free.
DA 2 gives the squad leader task 102 with event 1 (`+0x264`) for a player-faction target. **Verified** (probe, 124 AI_TASK records):
no base-data task uses the enums 102, 159 or 161. The `isDialogDelivered` evaluator (world-state ids 109/110, `FUN_140610250`) is **Unknown**.

### Tokens

`/NAME/` pairs are expanded until none is left (`FUN_14067a530`). Built in: `COST` (val0 of the line's first DA 8 or 9, else of its
first DC 2), `MYNAME` (speaker), `YOURNAME` (target, else "you"), `TARGETFACTION` (target's faction, else "you"), `PLAYERFACTION`. Any
other name: the WORD_SWAPS record of that trimmed name, its lines chosen like children (target = the partner), one text; no match → empty.
A token at the start or after ". ", "! ", "? " is capitalised. A line with DA 8 gets " [c.N]" appended. The `censorship` setting swaps a
fixed list of swear words. **Verified** by probe: 289 token names in the data, `HIM`, `OUTLANDER`, `FLATSKIN`, `BROTHER`, `YOURNAME`
the most used.

### Memory and saves

- `repetition limit` last-use time lives on the shared line, so **the cooldown is global across characters**; not saved.
- `chance permanent` rolls are per character; not written by `Character::serialise` (fcs.def's "saved forever" was not found).
- **Locks** are per character (default the DIALOGUE's `locked`) and **saved** in the character's save record as pairs `lok0`, `lok1`, ...:
  the string is the DIALOGUE string id, the bool of the same key "locked" (writer `FUN_140679900`, reader `FUN_14067f780`).
- `one at a time`: fails while another member of the speaker's squad runs the same root.
- Per-event counters and last targets are memory only.

## Conditions

`FUN_140676950`. `who` 0 = the speaker, **any other value = the target** (`FUN_140678f30`). With no target a non-me condition takes a
nearby player character (and stores it as the target). `B` = value > 0.
- **Bool** conditions return computed == B (compare-by ignored; value 1 must hold, 0 must not).
- **Num3**: compare-by 1 `<`, 2 `>`, else `==`. **Num2**: compare-by 1 `<`, else `>=`.

| DC | Computes (S = subject, Me = speaker) | Style |
|---|---|---|
| 1 RELATIONS | Me's faction's relation to S's faction (−100..100) | Num2 |
| 2 PLAYERMONEY | the player faction's money, any `who` | Num2 |
| 3 REPUTATION | a float on S's faction relations (meaning **Unknown**) | Num2 |
| 4 / 5 CARRYING_BOUNTY_ALIVE / DEAD | S carries a living / dead body with a bounty for Me's faction (or a diplomat of a hostile faction) | Bool |
| 6, 15, 34, 51 | not evaluated, always pass | - |
| 7 / 8 IMPRISONED_BY_TARGET / OTHER | S caged by Me's (7) or another (8) faction | Bool |
| 9 IS_A_TRADER | S's squad trades | Bool |
| 10 FACTION_RANK | the player's rank in Me's faction ([faction rank](factions-squads-towns.md#32-faction-rank)), any `who` | Num3 |
| 11 BUILDING_IS_CLOSED_AND_SECURED | a building of Me's (which one **Unknown**) | Bool |
| 12 PLAYER_TECH_LEVEL | world tech level | Num3 |
| 13 NUM_DIALOG_EVENT_REPEATS | Me's counter for the current event | Num3 |
| 14 IS_IMPRISONED | S is caged | Bool |
| 16 TARGET_IN_TALKING_RANGE | same area as Me and within 260 | Bool |
| 17 IN_MY_BUILDING | S is inside Me's building | Bool |
| 18 TARGET_LAST_SEEN_X_HOURS_AGO | Me's squad memory of S, hours; never seen: the hours since the game began (engine reading, **Observed** only that most event 3 and 67 lines need it large) | Num2 |
| 19 IS_LEADER | S leads its squad | Bool |
| 20 MET_TARGET_BEFORE | Me's squad remembers S | Bool |
| 21 / 22 WEAKER / STRONGER_THAN_ME | a strength rating ×1.5 apart (rating **Unknown**) | Bool |
| 24 IS_ALLY / 25 IS_ENEMY | relation ≥ 50 (or allied, or own) / ≤ −30 and not own | Bool |
| 27 BROKEN_LEG, 28 BROKEN_ARM, 29 DAMAGED_HEAD (< 0.2), 30/42 NEARLY_KO (< 0.1), 41 IS_KO, 44 IS_DEAD | S's body | Bool |
| 31 IN_A_NON_PLAYER_TOWN | S in a town not owned by the player | Bool |
| 32 IS_RUNNING | S runs (Me: always B) | Bool |
| 33 COPS_AROUND | Me senses law enforcement | Bool |
| 35 SQUAD_SIZE, 37 NUM_BACKPACKS | S's squad | Num3 |
| 36 IS_PLAYER | S's faction is the player's | Bool |
| 38 SQUAD_ONLY_ANIMALS, 43 SQUAD_IS_DOWN, 72 MIXED_GENDER_GROUP | S's squad | Bool |
| 39 IS_OUTNUMBERED | two head counts ×1.3 (which is which **Unknown**) | Bool |
| 40 / 47 BOUNTY_AMOUNT_PERCEIVED / ACTUAL | S's bounty with Me's faction (perceived: only while recognised) | Num2 |
| 45 IS_FEMALE | S's race is female | Bool |
| 46 CARRYING_SOMEONE_TO_ENSLAVE | S carries a living adventurer, bandit, slave or civilian (false for value 0 too) | asymmetric |
| 48 IM_UNARMED | S has no weapon | Bool |
| 49 TOWN_HAS_FORTIFICATIONS_WALLS, 57 WITHIN_TOWN_WALLS, 58 TOWN_WALLS_LOCKED_UP | S's town walls | Bool |
| 50 TARGET_IS_MY_MISSION_TARGET | S is Me's squad's mission target | Bool |
| 52-55 LOVE / HATE / SCREW_OVER / HELP | personality functions (**Unknown**) | Bool |
| 56 IN_COMBAT, 67 IS_SNEAKING (not in combat), 71 STARVING (nutrition < 1.7), 79 HAS_ROBOT_LIMBS | S | Bool |
| 59 IS_SLAVE (state 1/2), 62 IS_ESCAPED_SLAVE (state > 1), 61 TARGET_IS_SLAVE_OF_MY_FACTION | S's slave state | Bool |
| 60 HAS_A_BASE_NEARBY | a town of S's faction within 23040 of Me | Bool |
| 63 IS_IN_LOCKED_CAGE, 64 WEARING_LOCKED_SHACKLES | S's lock | Bool |
| 65 IS_SAME_RACE_AS_ME | S's race group = Me's | Bool |
| 66 CAN_AFFORD_BOUNTY | S is a player character and the player's money ≥ its bounty | Bool |
| 68 IS_INDOORS | S indoors | Bool |
| 69 HAS_ILLEGAL_ITEM | S's squad carries an item illegal for Me's faction or town; posts "{1} has been discovered smuggling {2}" | Num2 on 0/1 |
| 70 USING_MY_TRAINING_EQUIPMENT | S trains on Me's faction's dummy | Bool |
| 73 TOWN_LEVEL_CURRENT_LOCATION / 74 PLAYERS_BEST_TOWN_LEVEL | player town level (73 false outside a player town) | Num3 |
| 75 IN_A_PLAYER_TOWN | S in a player-owned town | Bool |
| 76 TARGET_CHARACTER_EXISTS | there is a subject | Bool |
| 77 IS_RECRUITABLE | false at a player head cap (**Unknown**), for a quantity ≥ 25 (**Unknown**), for law, trader, diplomat; military → !B; others B | Bool |
| 78 HAS_AI_CONTRACT | Me's squad runs a contract | Bool |

## Effects

`FUN_140680560(dialogue, line)`, run when a line is said or a reply clicked, in this order:
1. `unlocks` (listed unlocked, **and the current dialogue locked**), `unlock but keep me` (listed unlocked), `locks`: the speaker's own
   lock map.
2. `interrupt` stored as the pending event-1 dialogue.
3. `change relations`: each listed faction's relation **toward the player faction** += val0 ("Relations with {1} improved by {2}" /
   "decreased by {2}").
4. `crowd trigger`: unless already event 74, the owner's squad members (alive, awake, not animals) are forced to start that DIALOGUE
   (`FUN_140683f90`, force set) with event 74 at the target; whether the owner is skipped is not visible in the decompile. Its val0 %
   is not read here (**Unknown** where). **Verified** (probe): 93 uses, all val0 100 but two (50, 60).
5. `give item` to the target (val0 2..49 = stack count; "Received" + the item's name). **Verified** (probe): 45 uses, food, kits, notes.
6. `trigger campaign` (if the speaker's squad is not in one) at the target's home town or the player town nearest the speaker
   ([campaigns](factions-squads-towns.md)).
7. The DA effects in data order (below).
8. Non-player speaker squads only (`FUN_140273330`): `change AI` (`FUN_140272430`: the squad's packages replaced by that AI_PACKAGE,
   every member's AI reset, the package built with no target), then `AI contract` (`FUN_140272740`: that package run as the contract,
   val0 hours, aimed at the dialogue target). **Verified** (probe): `change AI` 190 uses (168 "Escaping slave- run away solo"),
   `AI contract` 169 (Bodyguard 1-72 h, escaping slaves 1 h, "contract Mercenary outpost guard" 48-192 h). Bodyguard's goals
   "Bodyguard mission target" (enum 45) and "follow mission target" (44) target SQUAD_MISSION; its `contract end talk passive` is said
   on contract end (event 30).

A recruit into a full player squad ends the conversation with "It looks like your squad is a little full." and skips the rest.

### Recruiting

DA 3 / 18 with a player-faction target (**Observed**, `FUN_140680560` → `FUN_140692820` → `FUN_1407ff8b0`; a non-player target's
squad simply takes the speaker in, unless a target flag at +0x1d0 is set):
- **Cap**: refused (the "little full" line) when the player faction already has CONSTANTS `max faction size` characters (30 in the base
  data). The three CONSTANTS limits sit side by side: `max squad size` (20), `max squads` (10), `max faction size`; the HUD's
  "{1,num}/{2,num}" counters show the faction and squad counts against the first and last.
- **Which squad**: the character leaves its platoon and the player faction's member add (`Faction::vfunc_5` → `FUN_1406b9c60`) puts it
  into the player's active squad (`FUN_1407f5520`: the cached selection squad, else the faction's first platoon), or, when that squad
  already holds `max squad size` members, into a new platoon of the SQUAD_TEMPLATE "blank squad" (`FUN_1407f4420`: made, added to the
  faction's platoon list at +0x210/+0x218). No `max squads` check there.
- **Before the loop**, when the active squad's count plus the recruits exceeds `max squad size` and the faction already has `max squads`
  platoons, `FUN_1407f4420` also makes one platoon of the active squad's template (disassembly `1407ff928`-`1407ff978`); the recruits
  still go through the rule above, so that platoon stays empty (**Observed**, static).
- **Each recruit**: speed mode set to 2 (RUN; the same field DA 23 writes), NPC class (`+0x1a0 -> +0xdc`) set to 9 ADVENTURER (the
  value every player-faction character gets when made, `FUN_14062b210`); a character without its own dialogue package (+0x248) gets
  its record's `dialogue package player` (none: the player faction's `dialog default`) as its dialogue; every armour piece it carries
  loses its `uniform` faction (Armour+0x1e8, [economy](economy.md#5-runtime-unit-price-item-virtual-slots-81-and-82) prices uniforms at 0.3x).
- DA 3 then opens the character editor on the recruits (mode 1, [characters](../characters.md#character-editor)) and selects the first.
- **Verified** (base data scan, 2026-10-18): 332 DIALOG_ACTION records of DA 3 / 18, used by 250 lines in 125 dialogues (hire offers
  such as "hire me 2500", freed prisoners and escaped slaves asking to join, unique recruits such as Crumblejon), reached from mercenary
  and unique CHARACTER records and dialogue packages (drifters, beggars, bandits).

| DA | Effect |
|---|---|
| 1 TRADE | ends the conversation, opens trade with the speaker |
| 2 TALK_TO_LEADER | a player target: the speaker's squad leader gets task 102 on the target |
| 3 / 18 JOIN_SQUAD_WITH_EDIT / FAST | the speaker joins the target's squad (player: recruited, its package becomes `dialogue package player`; 3 opens the editor, [characters](../characters.md)) |
| 4 AFFECT_RELATIONS, 5 AFFECT_REPUTATION | speaker's faction toward the target's: relation += val0 (clamp ±100) / trust |
| 6 ATTACK_CHASE_FOREVER, 55 ATTACK_STAY_NEAR_HOME, 57 ATTACK_IF_NO_COEXIST | the speaker's squad marks the target enemy; task 101 (55: 248); 57 only without coexistence |
| 7 GO_HOME | task 19 |
| 8 TAKE_MONEY | the target's faction pays val0, no balance check ("Paid c.{1}") |
| 9 GIVE_MONEY | the target's faction gets val0 |
| 10 PAY_BOUNTY, 30 ARREST_TARGETS_CARRIED_PERSON, 49 CLEAR_BOUNTY | [economy 11.4-11.7](economy.md#114-claiming); 30 only when the speaker has a town and the target is in it; 49 erases two ledger entries (the speaker's and the target's town's or jailer's booking faction) |
| 29 ARREST_TARGET | target in the speaker's town and not an animal: the speaker gets tasks 240 (capture) and 112 (find a cage), the target squad remembers the speaker (flag 5); else the speaker squad's memory flag 4 and task 4 (attack) on the target. **Verified** (probe): 176 uses ("You're under arrest!", "Thief!") |
| 11 CHARACTER_EDITOR | ends the conversation, opens the editor on the target |
| 12 FORCE_SPEECH_TIMER | speech timer = val0 |
| 13 DECLARE_WAR, 14 END_WAR | speaker's faction vs the target's (war: relation −35, "War breaks out..."; peace: up to −25) |
| 15 CLEAR_AI | drops the speaker squad's extra packages (lists **Unknown**) |
| 16 FOLLOW_WHILE_TALKING, 17 THUG_HUNTER, 34 RUN_AWAY, 52 SUMMON_MY_SQUAD | tasks 164, 165, 280 (from the target), 67 (squad to the speaker) |
| 19 REMEMBER_CHARACTER, 20 FLAG_TEMP_ALLY, 21 FLAG_TEMP_ENEMY | the speaker squad's memory of the target |
| 23 MAKE_TARGET_RUN_FASTER | the target runs |
| 25 TAG_ESCAPED_SLAVE | the target's slave state 2, its AI reset (9 uses) |
| 53 REMOVE_SLAVE_STATUS | a target in shackles or slave state: "Free!" floated in green over it and `Character_Unlock_Success`; slave state 0, AI reset, worn shackles unlocked (2 uses) |
| 24 GIVE_TARGET_MY_SLAVES | the characters on the speaker platoon's slave list are let out of their cages (if imprisoned) and join the target's platoon (`FUN_14032e430`) (5 uses) |
| 47 SURRENDER_NON_HUMANS | members of the target's faction within 100 of the speaker whose race is not the speaker's (`FUN_14042ecd0`: the race or one of its sub-races, race +0x40 / +0x120) become slaves (`FUN_14032e590`: slave state 1, the owner handle stored) and remembered by its squad; the tests are only partly read (8 uses, Holy Nation) |
| 40 ENSLAVE_TARGETS_CARRIED_PERSON | slavery ([carry-loot](carry-loot.md)) |
| 28 SEPARATE_TO_MY_OWN_SQUAD | the speaker (or a player-speaker's target) leaves for a new squad |
| 32 ASSIGN_BOUNTY | bounty += val0 for the speaker's faction, or books a pending crime |
| 33 CRIME_ALARM | the target is hostile, its crime timer 30 s, the town alarm may be raised |
| 35 INCREASE_FACTION_RANK | the speaker's faction's rank +1 while below val0 (0: no cap) (**Verified** by code) |
| 36 LOCK_THIS_DIALOG | locks the current dialogue |
| 37 / 38 / 39 ASSAULT / RETREAT / VICTORY_PHASE | the speaker's campaign phase: 1 (2 without a pt2 leader package), 2, 3 (unhandled) ([campaigns](factions-squads-towns.md#campaigns)) |
| 41, 42, 43, 48 CHOOSE_* | the character trading window (slaves sold / bought, bail, animals; val0 price override), the speaker as the slaver; 41 / 42 do nothing when the target is the speaker ([economy 12](economy.md#12-slaves-prisoners-animals)). Base data: "trade slaves" (slaver bosses) and "Wandering slave trader" carry 41 and 42, Rebirth's boss 41, "Empire pro-slave" 42 with val0 1000 (**Verified** by probe) |
| 54 OPEN_NEAREST_GATE | opens the speaker's town's nearest gate |
| 56 MASSIVE_ALARM | every squad of the speaker's faction attacks and fires event 66 |
| 58 KNOCKOUT | a speaker field = val0 (probably KO time) |
| 22 (empty), 26, 27, 31, 44, 45, 46, 50, 51, 59 | nothing in this executor |

## UI

**Verified** (layout files).
- `Kenshi_ConversationPanel.layout` (layer Middle, full-screen root, `position_real`): `NpcPanel` (Kenshi_FloatingPanelSkin, 0.25,
  0.6019, 0.4844 x 0.1389) with `NpcPortrait`, `NpcName` and the `NpcDialog` EditBox in a Kenshi_GenericTextBox frame;
  `PlayerPanel` (0.3542, 0.75, 0.2891 x 0.2519) with `PlayerPortrait`, `PlayerName`, the `PlayerDialog` ScrollView (choices),
  `MoneyLabelText` / `MoneyAmountText` ("c. {1,num}"); eight Kenshi_ConnectBarSkin bars.
- `Kenshi_SpeechPanel.layout` (layer Dialog, no input): Kenshi_SpeechPanelSkin (0.2344 x 0.0222) with `SpeechText` (Kenshi_WordWrapEmpty,
  centred, black shadow).

Code (**Observed**): the window (`FUN_140725740`, opened by `FUN_140727820`) names the speaker and the player character and renders
both portraits; choices use the MyGUI colours `DialogueTextOption` / `DialogueTextOptionHover`; the NPC frame grows to its text. The
bubble (`FUN_140725f30`) uses Kenshi_StandardFont_Large or _Medium, sits 4 units above the "Bip01 Head" bone, and fades from opaque at
1000 to gone at 2500 units from the camera (distance ×0.5 for large, ×0.75 for medium events). Meitou draws it as that layout
(`PlayerInterface.AddSpeech`): the skin and `DialogueBubbleText` #e6e6e6 with a black 1 px shadow, fitted to the wrapped text (the original's sizing is **Unknown**).

## Unknowns

T_WHOLE_SQUAD fan-out; how a childless closing line is shown in a player conversation; `isDialogDelivered`; where `crowd trigger`'s %
is read; DA 44-46; the KO event mask; the interjector pick rule; DC 77's cap and quantity; DC 3, 21/22, 39, 52-55 formulas; DA 15's
lists, 58's field; the speech timer's unit.

## Meitou design

As built: [simulation](../simulation.md#dialogue). Speech bubbles (`DialogueSystem.Ambient`, `PlayerInterface.Speech`): ambient lines are said as bubbles over the speaker's head, 2 + 0.12 x length seconds apart, the chain passing between speakers as in the original (the target's lines said by the target, interjectors by interjectors; a non-monologue root stops after one line). Started by the campaign announcement (event 2), crowd triggers (event 74), a contract's end talk (event 30), `DialogueEvent` commands from other systems, and a sight stand-in (`DialogueSettings.AmbientEvents`, on in the game, which also gates the commands' event lines): every 30 ticks an awake, free NPC within 600 units of a player character says event 66 (its faction hostile to the player's), 67 (ally) or 3 (else) to that player character, or, when it said a line to it in the last half game hour or none passes now, event 3 to the nearest NPC of another squad within 260 (DC 16's talking range); at most 2 per look; a squad's half-hour cooldown per target starts only once a line is said, so a failed `chance temporary` roll is tried again next look, as the original's perception fires the event on every update (the Red Sabres' attack root is 20%) (engine choices: the original's perception tests are only partly read, and NPCs talk only near a player character, where they can be heard).

Engine choices: a crowd trigger leaves out the owner and the speaker and rolls its val0 % per member; items that do not fit are dropped at the target's feet; contracts and `change AI` start on the next tick, and a contract's goals aimed at the squad mission follow the employer as squadmates follow a leader; DA 29 is an arrest without a fine in the speaker's town and an attack elsewhere (`ArrestSystem`); DA 53's "Free!" is a bark, not a green float. Events fired from other systems: 18-22 (first aid), 13 (shackles freed, a cage's lock picked), 41-43, 8, 17, 34, 57, 58 (the first witness of a pending crime, `CrimeSystem`), 70 / 71 (a severed limb), 23 / 24 (seen on the ambient cadence: a character down at the last look and up now; 24 when fighting or a hostile awake character is within sight), 60 (an NPC's leash gives up a chase, at its target), 46 (a jailer's release round), 44 / 45 (the police's spotting; the fine, the bail list and the surgeon's editor are the data's lines, [simulation](../simulation.md#arrest-and-jail)); DA 33 CRIME_ALARM does nothing yet. DA 2: the leader walks up (`DialogueSystem.Approach`) and opens the event-1 conversation within talk range, quietly when it has nothing to say; after 25 game minutes it gives up with event 60. DA 47 compares RACE records only (sub-races not read). DA 24's slave list is the speaker squad's members in slave state; they keep it, owned by the target's faction. DA 41 / 42 end the conversation and open the slave window as it closes ([simulation](../simulation.md#arrest-and-jail), which also fires event 51); DA 48 is not built. Contracts and `change AI` are saved on the PLATOON (`AiSave`, [save](../formats/save.md#npc-platoons-meitou-mapping)); a wild platoon's are not. Not built: event 25, 31 (betrayal), 36 from the witness function, the witness cases' extra tests.
