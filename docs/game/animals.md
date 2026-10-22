# Animals

Original ANIMAL_CHARACTER76/race/CharStatsAnimal/body/weapon/animation research; evidence scopes
and fields retained. Animal records substitute for CHARACTER in squad animals/animals2 and saves.
[Stats](character-stats.md), [combat](combat.md), [population](factions-squads-towns.md).

## Stats, body and techniques

Race controls anatomy/blood/hunger/speed/footprint; forelegs are ARM parts. Age/scale/HP-mult and
animal-strength use animal factory/race factors, not unmodified human stats/body. Creature
WEAPON category/natural technique flags differ from human fists; no block for surveyed weapons.
Weapon-level references manufacturer. Attack-type can disagree with weapon category and was
not found as an exe string (**Observed** editor-only inference, not all-mod guarantee).
Bonedog/Garru/Bull/Leviathan/spider/crab and other creatures have distinct clips/materials/limits.

Animal faction/hostility/run-away ratio applies ordinary relations; senses/blood/egg behaviours
are separately decoded/uncertain. Race carriable gate governs lifting (Leviathan excluded in
survey), not an invented mass threshold. Death items ([below](#death-items)), backpack and natural weapon are record inputs.
Appearance uses animal mesh/fur/body scale; race-specific animation names and bounds matter.
Pose/LOD distances can scale by animal size in engine, not original scheduler proof.

## 1. Records

- **ANIMAL_CHARACTER** (type 76) is the animal's record, used where a CHARACTER would be (squad template `animals`
  / `animals2`, [factions-squads-towns.md 5.1](factions-squads-towns.md); saves name it as the character's
  `target`, [save.md](../formats/save.md)). The install's load order has 43 of them (**Verified**, counted).
- Fields (**Verified**, the union over the 43): `combat stats`, `unarmed stats`, `stealth stats`, `ranged stats`,
  `strength`, `stats randomise` (as on CHARACTER), `animal strength` (int), `HP mult`, `scale min`, `scale max`,
  `lifespan`, `smell blood`, `smell eggs`, `turn rate`, `attack type`, `female chance`, `inventory w/h`, the
  bounty fields, `unique`, `faction importance`, `sounds`; references `race` (every record, value 100), `weapon`,
  `weapon level`, `stats` (only the Leviathan), `faction` (only the Landbat), `animations`, `strafe anim`,
  `turning anim`, `death items`, `backpack`, `AI Goals`, `bird attractor`.
- The `race` is an ordinary RACE record (Beak Thing, Bonedog, Garru, Bull, Goat, Leviathan, Spider, Crab...), with
  the anatomy, blood, speeds and footprint of [character-stats.md "Races"](character-stats.md#races); the
  animals' forelegs are ARM parts ([character-stats.md](character-stats.md)).
- `weapon` is a WEAPON record of a creature `skill category` (9 ELEPHANT .. 20 GIRAFFE, [combat.md
  "Enums"](combat.md#enums-from-the-editors-decompile-facts)): `_dog type weapon` (10), `_giraffe type` (20),
  `_bull type` (20), `_elephant type` (9), `_garru` (17), `_gorillo` (16), `_duck type weapon` (15), `_spider
  type` (13), `_robot spider type` (12), `_skimmer` (13). None can block. Lengths 18 (duck, small spider) to 120
  (elephant). Swamp Turtle and "Pack Beast Dead" name no weapon (**Verified**, dump).
- `weapon level` is a WEAPON_MANUFACTURER: "Ancient" on all but the Skimmer ("Unknown") (**Verified**).
- `attack type` (an int in the creature category range) does **not** always match the weapon's category: Wild
  Bull 11 with a category-20 weapon, Cage Beast 14 and Landbat 19 with the dog weapon (10). The string "attack
  type" does not occur in `kenshi_x64.exe` (**Observed**: absent from the binary's string table), so the game does
  not read it; it is an editor-only field.

### 3. Body

`CharacterAnimal` (**Observed**, vtable `1416f2208`):

| Character slot | Human | Animal | Used by |
| --- | --- | --- | --- |
| 0x238 (`vfunc_71`) | 1 | `HP mult` (`+0x6dc`; read by `14062f000`, 1 when 0 or below) | part body scale (+0x5c), blood capacity |
| 0x250 (`vfunc_74`) | the humanoid stats object | null | blood capacity uses s = 1; the hit path cannot sever limbs |
| 0x390 (`vfunc_114`) | 1 | lerp(`+0x700`, `scale min`, `scale max`) | part HP multiplier (+0x58, rewritten each tick), blood capacity, hunger rate |
| 0x398 (`vfunc_115`) | 1 | `+0x700` | the stats multiplier `+0x34` |

### 7. Animations

| Races (records) | Skeleton (bones) | Movement | Idles | Down | Attacks (techniques) | Unused |
| --- | --- | --- | --- | --- | --- | --- |
| Bonedog, Boneyard Wolf | dog (42) | **walk** 1.1, **run** 0.67 | **idle2** 5.97, **idle3** 6.0, **idle noloop** 2.53 | **gotosleep** 0.83, sleep 2.0 | attack1 0.77, attack2 0.83 | eat (listed) 2.63, strafeleft |
| Beak Thing | mammal_beak (52) | **walk** 1.87, **run** 1.07 | **idle1** 8.0, **idle2** 10.2 | **gotosleep** 3.17, **sleep** 5.67 | attack1 5.0, attack2 5.0 | mouthclose, mouthclack, eat (listed), strafe/turn left/right |
| Goat | goat (52) | **walk** 1.33, **run** 0.67 | **idle1** 11.3, **idle2** 15.3, **idle3** 12.3 | gotosleep 1.1, sleep 1.33 | attack1 0.97 | strafeleft/right |
| Garru | pack_beast (44) | **walk** 1.07, **run** 0.53 | **idle1** 3.67, **idle2** 3.67 | gotosleep 1.17, **sleep** 5.23 | attack1 0.77, attack2 0.83, attack3 2.83 | strafeleft |
| Bull, Bull (domesticated) | bull, bull_nohorns (37) | **walk** 1.6, **run** 0.67 | **idle1** 15.9 | **gotosleep** 1.47, **sleep** 2.57 | attack1 2.23, attack2 1.47 | strafeleft/right |
| Leviathan | whale2 (44) | **walk** 2.17 | **idle1** 15.0, **idle2** 12.0 | gotosleep 2.03, sleep 2.17 | attack1 2.43, attack2 1.63 | strafeleft, turnleft |
| Big-Bones (Cleanser Unit) | Big-Bones (44) | **walk** 2.17 | **idle1** 15.0, **idle2** 12.0 | none | attack1 2.43, attack2 1.63 | strafeleft/right |
| Swamp Turtle | elephant_turtle (40) | **walk** 2.13 | **idle1** 15.0, **idle2** 12.1 | gotosleep 1.27, sleep 1.7 | attack1 2.37 | strafeleft |
| Gorillo, Black Gorillo | frog_gorilla (43) | **walk** 1.33, **run** 1.13 | **idle1** 1.33, **idle2** 3.0 | none | attack1 1.2, attack2 1.03, attack3 1.33 | eat (listed), strafeleft |
| Crimper | Crimper (43) | **walk** 1.33, **run** 0.68 | **idle1** 1.33, **idle2** 3.0 | none | attack1..3 as the gorillo | eat (listed), strafeleft |
| Swamp Raptor (Megaraptor), River Raptor | swamp_duck, swamp_duck_nospikes (59) | **walk** 1.0, **run** 0.67 | **idle1** 6.33, **idle2** 3.83 | gotosleep 1.33, sleep 0.67 | attack1 1.27 | eat (listed), strafeleft |
| Spider, Small Spider | flesh_insect (58) | **walk** 2.67 | **idle1** 1.33 | **gotosleep** 1.33, **sleep** 0.67 | attack1 1.83 | eat (listed), strafeleft/right |
| Crab (Barnabus) | Crab (36) | **walk** 2.67 | **idle1** 1.3 | none | attack1 1.83 | strafe |
| Robot Spider Worker / Guard | robot_worker (36) | **walk** 2.67 | **idle1** 0.03 | none | attack1 1.83 | strafeleft/right |
| Land Bat | land_bat (52) | **walk** 1.33, **run** 0.67 | **idle1** 11.3, **idle2** 15.3 | none | attack1 0.97 | idle3, eat (listed), strafeleft/right |
| Cage Beast | cage_beast (41) | **walk** 1.6 | **idle1** 15.9 | gotosleep 2.5, sleep 1.5 | attack1 1.4 | run 0.67, strafeleft |
| Skimmer | skimmer (32) | **walk** 2.67 | none: its `idle1` record names a clip the skeleton lacks (it has `idle01` 0.17) | **sleep** 0.17 | **attack1** 1.83 (its `eat (attack1)` record too) | strafe |

## Death items

The meat, skins, teeth (animals) and cores/CPUs (robots, Gurglers) a body yields. Not dropped at death: they are in the inventory
from the start.

- `death items` (ANIMAL_CHARACTER and CHARACTER) lists ITEMs, value 0 the quantity, the others 0 in the install (**Verified**,
  merged data: 43 animals, e.g. Bonedog 1 Animal Skin / 4 Animal Teeth / 3 Raw Meat; Pet Dog, Settler's Pup, Pack Beast Dead none).
- Added when a new character's inventory is set up, after the `inventory` list (`Character::vfunc_88` `FUN_140622e80`, called last
  by both setup functions, not for an inventory loaded from a save); entries below 1 skipped; each item gets the byte `+0x138`
  set (**Observed**, decompile).
- `+0x138` is the saved item key `death` (read/written by `FUN_140761550` / `FUN_1407609e0`) (**Observed**). In saves these items
  take ordinary grid cells beside carried goods, each entry one stack of its whole quantity whatever the item's `stackable`
  (Electrical Components 4, Iron Plates 6, `stackable` 1), and a stack of the same record without the flag stays a separate stack
  (**Verified**, METARU and quicksave platoon files: 144 / 187 flagged items, none overlapping).
- Item lookups by type (`FUN_1407465d0`, used by `Task_grabOneFood` and `CharacterAnimal::vfunc_94`) skip flagged items while the
  owner is alive (`+0x5bc`) (**Observed**).
- Taking one out (`FUN_14075e7e0`, from the inventory's remove functions) clears the flag; when the owner is alive and down
  (`isDown`, [carry-loot.md](carry-loot.md)) the owner dies (the death function) with "{1} has been butchered" (**Observed**).
- No inventory-window filter on the flag was found: the items show while the owner lives (**Observed**, absence in the decompile).
- Unknown: `FUN_1405a59a0` (reads record 3979's `death items`); whether the flag changes weight.

Meitou: `ItemFactory.Build` adds them (`ItemInstance.Death`), stacks merge only with the same flag, the feed search skips them,
moving an item out of its inventory clears it, and a loot take that removes flagged units from a live body butchers it.

## 8. Not found

How the game levels the natural weapon item (`14062aa60` makes it from `weapon` and the `weapon level`
manufacturer without a level argument; the default was not traced, **Unknown**), the consumers of `animal
strength`, lifespan and the smells, how an animal ages (the writer of `+0x700`), animal AI goals.
