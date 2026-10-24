# Economy

Kenshi1.0.68 research from ItemValue/trade/shops/town/crime binary functions, schema and merged
records. Tables retain constants, price classes and surveyed fields. `Meitou.Data.Gameplay.Economy`
and Simulation Trading/Crime implement researched rules; [carry-loot](carry-loot.md) owns theft.
No skill/reputation/time-of-day price factor was observed in the decoded trader-factor path.

## Item value

**Verified** arithmetic: gear truncates after each multiplication; plain/ingredient prices round
floor(x+0.5). Quality fraction s=quality/100. Engine lerp arguments are (t,a,b).
Weapon: truncate value*manufacturer-price-mod, then truncate lerp(logistic,15,160)*value,
logistic=1/(1+exp(-4*(2s-1))); apply combined sword/global factor with truncation.
Armour: quality>0 and coverage uses s² interpolation of classLo*matLo to classHi*matHi;
otherwise own value. Apply armour/clothing category, relative-price and slot factors separately:
hat3=0.3, legs6=0.5, shirt8=1.5, boots9=0.2, others1. Quality0 uses own value, not low endpoint.
Crossbow107/limb111: s² between value/value1, category multipliers are swapped (robotics for107,
crossbow for111). Container46 uses own integer value without multipliers.

Ingredients recursively price same quality with category flag1; sum rounded
ingredientValue*(v0/100)*(1+profitability). **Observed** v0 interpretation; other steps verified
in disassembly. Plain artifact skips multiplier; food-crop uses food/global, other items use
trade/global, including non-trade items. Gear stores value at construction; generic value can
recompute. Runtime tables below retain class-specific sale/player-crafted/armour reductions.

## Units, stacks and traders

**Verified** sell1 means player selling to shop; sell0 buying. Generic unit: base value, town
market factor with truncation, loot fraction on sale unless unstolen trade item, charge fraction
when count1, finally trader factor. Stack total: p*n-trunc(p-p*charges/maxCharges), except count1
uses unit directly. The trade-window's split of exchanged-stock units remains **Observed/Unknown**.

**Observed** trader T=buyMult*culturePrice, halved for the decoded stolen-to-player-faction case.
**Verified** buyMult comes from trader squad template and applies both directions. Town culture
wins over faction, default1; duplicate culture entries last-win. Trade-price percentage=v0/100,
zero means100%. Culture also defines illegal/forbidden goods and buildings.
**Verified** (probe of the merged base data, 2026-10-24): no UC town has a culture, so UC traders use FACTION United Cities' `empire`
(also Traders Guild, Empire Peasants): Hashish 1200%, Bloodrum/Hemp 400%, Sake 150%, the first three illegal. Hashish elsewhere:
holy 400, slavers 600, shek 350, `empire SW override` towns 500, swamper 100.
**Observed** town market factor starts uniform[1-m,1+m), m0.3, then clamps against global-town
factors/default1 within distance/1e6*m*10. Actual setup-list membership/order remains unknown;
save factors span0.7..1.3. Only generic items use market factors; save v0=trunc(factor*10000),
load /10000, accepting legacy goods-key layout (**Verified**).

## Shops and transactions

Stock derives vendor/item lists and template count/money/restock fields, with culture-forbidden
items excluded. Squad and building shops have distinct setup paths. Keep stock/purse/refresh
state and inventories separate from displayed trade UI. Buy/sell checks availability, quantity,
charges, both parties' funds and inventory fit. Stolen goods require fence/catch logic; selling
success alone does not prove legal transfer. Restock, blueprints/research money, artifact and
money-item branches are scoped in the expressions/field tables and undecoded questions.

- **Stock grid** (**Observed**): the trader window shows its trader's `backpack_content` section ([ui-screens](ui-screens.md#6-inventory-trade-dialogue-build)).
  The base game's CONTAINER `Shopkeepers Backpack` (`910-gamedata.base`) has storage 20 x 20 (raw read of `gamedata.base`); which characters wear it is not traced.
  The window's grid is 18 x 18 for a squad trader and the building containers' merged size for a building shop (**Observed**, `FUN_140953850`; details in ui-screens).
  Meitou's squad shops hold one 18 x 18 stock (`Shop`). The 2026-10-17 reference screenshot's trader (Ronin Construction Trader) shows about 28 columns.
- **Stock stacks** (**Observed**, `ShopTraderInventory::vfunc_1` = `FUN_140955f50`, the stock section's constructor): stack bonus minimum 1, mult 50, so a stock cell holds
  50 x the item's `stackable` (Building Material, `stackable` 1: 50). Meitou: `Shop.StockStackMult` on both shop kinds.

### Trading range

(**Observed**, `FUN_14070d290`; its five callers are the one-unit move `FUN_140713d20`, the trade window `FUN_140715560` and `FUN_140712250`, `FUN_140712490`, `FUN_1407139a0`.)
An item may move between two inventories of different owners only within an X/Z distance of the owners' positions; a failure is result 1, "Out of trading range.",
with the sound `gui_notrade` (`FUN_14070f7e0`). The limit is 1000 unless a flag is set: the caller's flag (the one-unit move sets it when the target is a building's
inventory, `uses_building_category`), or an owner of type 1 whose +0x2f8 is 2, or of type 0x5b whose +0xc0 object has +0x2f8 = 2, or the source owner's +0x78 object passing
its slot +0x260 (meanings **Unknown**). Flagged: the owners must not be enemies (faction test) and must be on the same floor, and the limit is 60, or the source owner's bounds radius + 40
when the source is a building (class 0). Same owner: always allowed. **Unknown**: which windows the three drop callers flag; whether take-all uses the check.

Meitou (engine choices): window moves to and from a machine or storage need the character within `ProductionSettings.TransferReach` 40 of the building's footprint hull (for the
bounds radius + 40), to and from a container within `PlayerSystem.TradingRange` 60; both post "Out of trading range.". The loot window closes beyond 1.5 x the carry reach. A held item given to a
character: within `PlayerSystem.GiveRange` 1000 of its window's owner, `StealRange` 60 out of a non-player window. No faction or floor test, no sound (`gui_notrade` is in no bank, [audio](../audio.md)).

### Who trades

- `IS_A_TRADER` (dialogue condition 9, `FUN_1405cff70`) and the stock are the squad's: they read the SQUAD_TEMPLATE `is trader` through the character's platoon (**Observed**, `uses_is_trader`).
  The CHARACTER field `is trader` (fcs.def) is read by no function through its string (**Observed**: string xrefs list only the three platoon readers).
- A trade is opened by a dialogue line with effect 1 (DA_TRADE). In the data only the shopkeeper's dialogue has one (**Observed**, walk of the dialogue references): Thieves Guild, Shinobi
  Trader 3 lines, Thief Boss, Thief, Ninja Guard and the squad's `dialog squad` (TOUGH 1234) none; Skeleton Barman, Smuggler Barman, Stone Camp Trader yes; Shop Guard, Traders Guard, Slaver,
  Samurai no. Of 125 trader templates with vendors in the probe load order, 89 mark a member CHARACTER `is trader`.
- Meitou (the right-click Trade verb, engine): a member of a shop squad whose record sets `is trader` or whose dialogue reaches a Trade line (`DialogueSet.Trades`, conditions not checked).
- Shop squads with no such member (8 in the probe load order: bank, armory UC, holy Armoury, holy General storage, farm shop UC, Crab Man Shop, Shop bar black desert, Cannibal
  hunter hq) cannot be bought from in the original either (**Observed**, owner's play); their stock is only in their home's containers.

### Building shops (**Observed**, decompile)

- **Which** (squad factory `FUN_140583a10`): a squad whose template has `vendors` (or passes `FUN_140953f60`, **Unknown**; `is trader` is not tested) with a home building that made no
  prisoner or slave squads is filled by `FUN_14095b210` into its home; any other by `FUN_1404ff5e0`, which fills a squad stock only for `is trader` and otherwise the home's containers.
  A faction test (`+0x250 == 0`, meaning **Unknown**) gates both. So a resident without `is trader` (e.g. `flotsam refuge`, vendors `house contents`) gets its home's containers stocked.
- **Restock** (`ActivePlatoon::vfunc_4` calls `FUN_1404ff5e0(platoon, 0)` once `inventory refresh time` passes): the home branch skips a building whose interior object's `+0x10` is set
  (meaning **Unknown**); the first fill passes 1 and never skips.
- Meitou, non-traders (`TradeSystem.FillHome`, engine choices): the stock rolls as a building shop's, then each unit goes into the first stock container with room (their own grids and
  stack bonus), what fits nowhere is lost, the containers are unlocked, the purse is untouched; refilled every `vendors refresh time` only while every container is empty.
- **Containers** (`FUN_1409562f0`): the home's interior objects whose functionality is 2 BF_RESOURCE_STORAGE, 9 BF_SHOP or 13 BF_GENERAL_STORAGE and that have an inventory.
  The filler unlocks their locks, sorts them (key **Unknown**), optionally clears them, puts special items first, then the vendor items; what fits nowhere is destroyed.
- **Count**: n = round(U(0.8, 1.2) x `vendors fill total amount` x wealth), at least 1. Items: the vendor lists share n by weight as for squads (`FUN_140959800`), but every list's
  money roll (`money item prob` against U(0, 1), then U[`money item min`, `money item max`]) is summed into one stack of the money item first.
- **Purse** (the platoon's money): `vendor money` < 1: trunc(clamp(0.25 x value of the placed items, 3000, 25000) x wealth); else trunc((`vendor money` + r) x wealth) with r an int from
  -0.4 x `vendor money` (the upper bound is not shown by the decompile).
- **Wealth** = clamp(faction wealth, 0.2, 1), but the faction wealth function (`FUN_1402dd530`) is a stub returning 1.0, so wealth is always 1
  (**Verified**, kenshi_x64.exe decompile: 9-byte function, constant return). It scales only shop stock count, purse and nest spawn weight; no price reads it.
- Meitou (`Shop.ForBuilding`, engine choices): the merged stock is the shop's one inventory (no per-container inventories yet), the money stack goes to the purse, r is symmetric,
  stock is saved on the platoon as squad shops' ([save keys](../formats/save.md#npc-platoons-meitou-mapping); the original keeps it in the containers, zone file). Observed with real data: Squin's armour shop (Bughouse) 18 x 287, Stack's bars (Storm House) 18 x 88.

## Crimes, bounties and prisoners

Ledgers are per wanted faction; crimes pending at deed time and witness booking are distinct.
CONSTANTS categories define theft/assault/murder/looting values. Expiry is0.04 hours/cat and
permanent from10000; capture, claiming, bail and prison sentences use separate paths.
Witness/innocence details remain partly unknown; a killing is no crime (11.6), only relation events.
Slavery/prisoner/animal transactions and loot generation are not equivalent to ordinary shop
trades. Consult [factions](factions-squads-towns.md), [carry-loot](carry-loot.md) and the retained
unknowns before implementing unsupported branches.

### 3. CONSTANTS price block

The loader `FUN_14086b2b0` reads the GLOBAL CONSTANTS record into the block at `142133dd0` (field address =
`142133dd0 + 4 * index`; the same loader as in [character-stats.md](character-stats.md#constants-used-by-this-subsystem)).
The consumer column is where the value is read. **Verified** for each row: the loader's read and a consumer found through the
data cross-references, and the base values from a probe over CONSTANTS; "[Editor]" is the `fcs.def` default.

| Index / address | FCS field | Base | [Editor] | Stored as | Consumer |
| --- | --- | --- | --- | --- | --- |
| 0x37 `142133eac` | `global price mult` | 1.0 | [1.0] | as is | only the seven rows marked "x global" below |
| 0x34 `142133ea0` | `armor price mult` | 0.6 | [1.0] | x global | `ItemValue` (armour with part coverage) |
| 0x38 `142133eb0` | `clothing price mult` | 1.0 | [1.0] | x global | `ItemValue` (armour without part coverage) |
| 0x35 `142133ea4` | `sword price mult` | 0.4 | [1.0] | x global | `ItemValue` (WEAPON) |
| 0x36 `142133ea8` | `trade price mult` | 0.6 | [1.0] | x global | `ItemValue` (plain items that are not `food crop`) |
| 0x28 `142133e70` | `food price mult` | 0.5 | [1.0] | x global | `ItemValue` (plain items with `food crop`) |
| 0x33 `142133e9c` | `robotics price mult` | 1.0 | [1.0] | x global | `ItemValue` (**CROSSBOW** records, see the swap below) |
| 0x32 `142133e98` | `crossbow price mult` | 1.0 | [1.0] | x global | `ItemValue` (**LIMB_REPLACEMENT** records) |
| 0x39 `142133eb4` | `trade profit margins` | 0.3 | [0.5] | as is | `Zones_TownSetup` (market factor spread, section 7) |
| 0x3a `142133eb8` | `loot price mult GEAR` | 0.25 | [0.15] | as is | sell fraction of weapons, armour, blueprints (slot 81) |
| 0x3b `142133ebc` | `loot price mult` | 0.5 | [0.4] | as is | sell fraction of maps, items, limbs (slot 81) |
| 0x3c `142133ec0` | `loot price mult player armour` | 0.25 | [1.0] | as is | sell fraction of armour the player crafted (`Armour::vfunc_81`) |
| 0x3d `142133ec4` | `loot price mult player weapons` | 0.25 | [0.5] | as is | same for weapons (`Weapon::vfunc_81`) |
| 0x3e `142133ec8` | `blueprint price mult` | 1.5 | [1.5] | as is | research cost (section 10) |
| 0x3f `142133ecc` | (none: the loader writes the constant 0.3 itself) | 0.3 | n/a | hard-coded | `Armour::vfunc_81` (see section 5) |

### 4.2 ARMOUR and clothing (type 3)

The class and material tables are the same two tables [combat.md](combat.md#armour-and-medical-hit) uses for defence, built by the
constructor `FUN_1408a8f90`; the price uses the **first value column** of each entry (offset +4), combat uses the later
columns. **Verified** (constructor stores and `ItemValue` loads read from the same four hash tables `142134ce0`,
`142134d20` (class, `ArmourClass`) and `142134d60`, `142134da0` (material, `ArmourType`)):

| `class` | name | `classLo` (quality 0) | `classHi` (quality 100) |
| --- | --- | --- | --- |
| 0 | GEAR_CLOTH | 100 | 5000 |
| 1 | LIGHT | 400 | 30000 |
| 2 | MEDIUM | 800 | 35000 |
| 3 | HEAVY | 400 | 40000 |

| `material type` | name | `matLo` | `matHi` |
| --- | --- | --- | --- |
| 0 | CLOTH | 0.2 | 0.8 |
| 1 | LEATHER | 0.5 | 1.0 |
| 2 | CHAIN | 1.0 | 1.5 |
| 3 | METAL_PLATE | 1.5 | 1.5 |

### 5. Runtime unit price: Item virtual slots 81 and 82

Every inventory item class has slot 81 ("value of one unit, with a sell flag") and slot 82 ("value of the whole stack").
RTTI gives the implementations (`rtti.tsv`; the slots 81 and 82 are at `+0x288` and `+0x290` of the vtable); **Verified** by reading each (also the slot 82 `FUN_1407915b0`, which calls slot 81 through `+0x288`). The byte `sell` is **1 when the item is being
sold to a shop** and 0 when it is being bought from one (**Verified** in the trade window `FUN_140715560`: the error
code is `(sell != 0) + 3`, i.e. 3 "You can't afford that." for a purchase and 4 "The shopkeeper can't afford that." for a
sale, and the same flag selects which price is computed).

| Class | Slot 81 (`sell` = 0) | With `sell` = 1 |
| --- | --- | --- |
| `Weapon::vfunc_81` `FUN_1408a52f0` | `trunc( T * stored )` | `trunc( T * stored * f )`, `f` = `loot price mult player weapons` if `Weapon+0x208` is set, else `loot price mult GEAR` |
| `Armour::vfunc_81` `FUN_1408a9c20` | `stored`, then if `Armour+0x1e8` is set `* 0.3` (the hard-coded 0.3 of section 3), then `trunc( T * . )` | the `stored` first gets `loot price mult player armour` if `Armour+0x208` is set, else `loot price mult GEAR`, and then the same 0.3 and `T` steps |
| `BlueprintItem::vfunc_81` `FUN_1402d30f0` | `trunc( T * value )` (`value` = the research `money` stored at `+0x1e8`, section 10) | `trunc( T * value * loot price mult GEAR )` |
| `MapItem::vfunc_81` `FUN_14075d950` | `trunc( T * value )` (the int at `MapItem+0x1e8`; where it is set was not traced) | `* loot price mult` |
| `MoneyItem::vfunc_81` `FUN_14001352a` (thunk to `FUN_14075f660`) | the record `value`, nothing else | same |
| everything else (`Item`, `InventoryItemBase`, `Gear`, `NestItem`, `ContainerItem`, `SeveredLimbItem`, `RobotLimbItem`) `FUN_1407a8f90` | see below | see below |

### 9.1 SQUAD_TEMPLATE fields (data = **Verified** by probe over 959 templates)

| Field | Meaning | Data |
| --- | --- | --- |
| `is trader` | the squad is a trading squad | needs a vendor list |
| `vendors` | list of VENDOR_LIST (val0 = weight of the list) | |
| `vendors fill total amount` | how many items the vendor holds (default 15 in the editor; 0 or below means 100 in the code) | 0 x279, 15 x397, 10 x14, 100 x19, 20-40 common, 1000 x1 |
| `vendor money` | the building-shop vendor's purse (building fillers only, see 9.2); 0 = derive from the stock | 0 x954, 1000 x4, 250 x1 (the five non-zero templates all come from mods of the probe load order, none from `gamedata.base`) |
| `vendors refresh time` | game hours between stock refreshes (<= 0 means 24000) | 24 x874, 12 x15, 48 x13, 72 x13, 70 x10, 99 x10, some 99999 |
| `buy mult`, `buys stolen`, `buys illegal` | section 6 and 8 | |
| `robotics levels`, `crossbow levels` | average grade g (ArmourRarity 0..5, editor default 1) of stocked limbs / crossbows. Limbs (`FUN_140955370`, **Verified**): weights g 100, g-2 22 (g > 1), g-1 44 (g > 0), g+1 44 (g < 5), g+2 22 (g < 4), an int roll against the running sums; the grade's 5/20/40/60/80/95 (`FUN_140952320`) is the limb's `level` (the factory's grade argument). Crossbows (`FUN_140955210`, **Verified**) likewise with g-2 20, g-1 33, g+1 33, g+2 5, only g when g is 5; a shop crossbow gets no manufacturer (the factory call passes none) | values not surveyed |
| `special items`, `special map items` | extra guaranteed stock (`read_special_items_n3` `FUN_140958740`) | not decoded |

### 11.1 Ledger

Per-character hash map keyed by faction (`Character+0xf0`). Entry (`FUN_1405e7ee0`): `+8` amount int, `+0xc` crime bitmask
(`1 << code`), `+0x10` claimed byte, `+0x18` double game-hour timestamp of the last raise (**Verified**: crime add, claim marker
`FUN_140854170`, save loader `FUN_140625e60` / writer `FUN_1406262b0` agree). Save keys: [save.md](../formats/save.md).

### 11.2 How a bounty is gained

- **Crime**: `FUN_140853ec0(crimeEvent, faction)`: if the crime code is nonzero and has not been processed yet (`+0xa4` flag), and the
  character's **`NPC class`** (`+0x1a0 -> +0xdc`, the same read as the police test, carry-loot.md 4.3) is not 8 (BANDIT),
  5 (DIPLOMAT) or 1 (LAW_ENFORCEMENT), then the crime's amount is **added** to a ledger entry chosen by `FUN_140852010` from the given faction: a caged character's prison
  owner's, else the given faction's **`legal system`** (`+0x38`, the first FACTION `legal system` reference; **Observed**, its only reader `FUN_1407f4f40`
  stores it there; e.g. Bounty Hunters and Medics Guild -> United Cities) when it has one, else the given faction itself when its `+0x40` flag is set (else none).
  `FUN_140851fb0(faction)` is the same choice without the cage rule ("booking faction" below).
  The given faction is the **booking police witness's** (or the police in the hit rule of 11.6), not the victim's (**Observed**: both callers pass their own faction); the crime bit is set, the timestamp is reset to now, and the "wanted" UI is refreshed. The very first crime
  code (1, Enslaving) sets the flag but adds nothing (**Observed**; the code returns before the add). The same function also sets the status float `Character+0x1a0 -> +0x110` to 250.0: the **"recently wanted" marker** that makes a bounty
  visible at once (11.8); setting or witnessing a pending crime (`FUN_140852e10`, `FUN_140851f40`) sets it too. Its decrement was not traced.
- **Crime amounts** (`FUN_140851e40`; **Verified**: a switch with no other inputs; names from the game's own name table `FUN_140856530`). The editor's
  `CrimeEnum` (FCS decompile) names the codes differently: 2 `CRIME_LOCKPICKING`, 6 `CRIME_ASSAULT_VIP`, 7 `CRIME_SLAVE_FREEING`, 9 `CRIME_TERRORISM`,
  14 `CRIME_FARM_EATING`; the rest match the game's labels:

| Code | Crime | Amount |
| --- | --- | --- |
| 1 | Enslaving | 100 (but see above: nothing is added) |
| 0xb (11) | Trespassing | 100 |
| 2 | Burglary | 1000 |
| 0xd (13) | Fencing | 1000 |
| 3 | Theft | 2500 |
| 0xc (12) | Escaping Prison | 2500 |
| 0x10 (16) | Uniform Theft | 2500 |
| 4 | Murder | 5000 |
| 7 | "Terrorism" label; editor `CRIME_SLAVE_FREEING` (given for lifting a prisoner out of a cage, [locks](locks.md)) | 5000 |
| 8 | Smuggling | 5000 |
| 9 | Terrorism | 5000 |
| 0xf (15) | Kidnapping | 5000 |
| 6 | Treason | 50000 |
| 5, 10 (Looting), 0xe (14, Crop Theft), others | Assault, Looting, Crop Theft, default | 500 |

- **Trespassing** (perception `FUN_140859680` -> test `FUN_140852350`, **Observed**): an NPC seeing a player character inside a
  building sets a pending Trespassing (code 11) against **its own** faction (victim: itself), fires dialogue event 17 and gives its
  faction's memory of the player (`FUN_140673a20` -> `FUN_140673930`: one entry for the whole player faction, a `SubjectiveTags`
  stamped with the time) the long-term tag 1 MY_INTRUDER ([dialogue](dialogue.md#enums), DC 23); no base-game line reads that tag
  (**Verified** by probe). Not tested when the witness regards the target as an enemy (Character `vfunc 125`) or an ally
  (`vfunc 126`: same faction, else `FactionRelationsIsAlly`), when the target is in a code-0xb place, or is chained to the witness
  (`FUN_14032e1e0`: target `+0x320` "chained mode" and the chain's hand id = the witness's). Two cases, both needing the residents'
  **"house locked"** flag (the owner platoon's AI `+0xe8`, saved as `homelok`, [locks](locks.md#doors)) unless noted:
  the **resident** case (the target is in the witness's own home): flag set, and the home is not public (vfunc 82, `public day`;
  or `+0xc4 != 1`, **Unknown**) or the hour is outside 7..22 (game clock `DAT_1421303d0`+0, the hour int). Same-town targets of
  NPC class 1, 2, 5 are exempt. The **police** case (a police-like witness, someone else's building): the building's faction does
  not regard the target's as an ally, the building is not public, and the target has a slave state or the owner platoon's flag is
  set; never when that platoon's leader regards the target as an ally. No hour test. The witness function then books it as any
  crime (amount 100). `FUN_140852240` fires event 17 without a crime (a squad of the witness's town; NPC side). What follows is the
  event-17 lines' data (**Verified** by probe: "Intruder!" with DA 6, "Leave my abode" with DA 12 then DA 33/34, "/GUARDS/!" with DA 33).
- **Spawn bounties** (`FUN_14062b210`, **Observed**): CHARACTER `bounty chance` vs an inclusive roll 0..100 (`roll < chance`);
  amount `trunc(bounty amount + 0.5*k)`, `k` an integer in `[0, 2*bounty amount fuzz]` (the fuzz only adds), for each
  `bounty factions` entry (else `defaultEmpireFactionSID`), added without a crime bit (`FUN_140853e20`). fcs.def: "Defaults to the
  empire"; 117 base CHARACTER records have a chance, 64 of them no `bounty factions` (**Verified** by probe). Whether the fuzz is rolled
  per faction is **Unknown**.

### 11.3 Lifetime

`FUN_140851f10`: `0.04 * amount` hours below 10000, else never ("Notorious"). The character tick `FUN_1405cbb80` removes at most
one expired entry per tick and is skipped while `Character+0x2f8 == 2` (imprisoned); each crime resets the timestamp (**Observed**).

### 11.4 Claiming

Two paths pay the player a captive's bounty (**Observed**). Both read the captive's entry for a booking faction, pay only when it is
nonzero and unclaimed, halve it (truncated) when the captive is **dead** (`+0x5bc`, [carry-loot](carry-loot.md)), add it to the player's
money, mark the entry claimed (it never pays twice) and show "Received c.{1} reward.".

- **Caging** (`FUN_140330d50`, called by `Task_PutInSomething` after a successful put-in): the *cage* is the first argument (its
  building function, vtable slot 94 = `Building+0x158`, must be 8 `BF_CAGE`; the editor's `BuildingFunction`), the carrier the second,
  the captive the third. Pays when the carrier is of the player faction, the captive is not, the cage owner's faction is not the
  player's, not `not real` and not BANDIT (`fundamental type` 8, `CharacterTypeEnum`), neither carrier nor captive has the escaped flag
  (status `+0xe8`), and not (cage faction SLAVER 7 or `FarmBuilding::vfunc_15(cage) +0xd8 == 5`, read as a town of type 5 `TOWN_SLAVE_CAMP`: the function walks the
  cage's parent chain to its root, not verified as the town) unless the carrier's status type (`FUN_1405c8aa0`: status `+0`, 1 slave, 2 escaped) is 0.
  The amount is the captive's bounty for the cage owner's faction; plays `Bounty_Fulfilled`. The same put-in also sets the sentence (11.5).
- **Dialogue** `DA_PAY_BOUNTY` (10) (`FUN_140680560` case 10): pays for the body the talking player carries, with the speaker's
  booking faction; then the speaker takes the body (tasks 213 `LIFT_PERSON_SNATCHING_ALLOWED` and 112 `FIND_CAGE_AND_PUT_IN`).
  Base data uses it in `EV_PLAYER_TALK_TO_ME` dialogues gated by `DC_CARRYING_BOUNTY_ALIVE` / `_DEAD` (e.g. "shek bounty desk"),
  together with `DA_ARREST_TARGETS_CARRIED_PERSON` (30) and +relations (**Verified** by probe of the merged data). The two effects come in either
  order (shek desk: alive 10, 30, 4; dead 30, 10, 4; **Verified** same probe), so the body is still carried when 10 runs. Meitou: [simulation](../simulation.md#arrest-and-jail).

### 11.5 Bail and sentence

- **Sentence** (`FUN_140854950`, the only reader of CONSTANTS `prison time` 1.0; reader **Verified**, formula **Observed**): set by
  `Task_PutInSomething` (`PUT_IN_CAGE` 108) only when the captive has a bounty with the cage owner's faction. It stores the start
  (now, double game hours) at ledger `+0x98` and the length at ledger `+0xa0` (float): `trunc(bounty * 0.02 * prison time)` hours,
  999999 above 25000, 16 when the bounty lookup returns 0. Saved as character keys `sentence` (start, truncated int) and `psts` (length,
  float) by `FUN_1406262b0` / `FUN_140625e60`.
- **Served** when `start + length <= now` (`FUN_1405c9f00`, also false for a character with a nonzero status type, e.g. a slave);
  nothing counts down. A prisoner caged without a bounty gets no sentence: whether the ledger then reads start 0 / length 0 (served at
  once, so goal 211 frees them immediately) is **Unknown** (the ledger constructor was not checked); Meitou should treat it as served. The status panel (`FUN_1405d2da0`) shows "Imprisoned, sentence time remaining: ceil(length - elapsed) hrs",
  "FOREVER" for a length >= 2000, else "Waiting to be released" (**Observed**). Bounty expiry is paused meanwhile (11.3).
- **Release** (`Task_ReleasePrisoner` 110, `FUN_1403570a0`; **Observed**): the jailer goal is AI_TASK "police free prisoners" (enum 211,
  only in AI_PACKAGE "addon- law enforcement"; the "- no freeing prisoners" variant lacks it; **Verified** by probe). Its finder
  `FUN_1408cf3c0` picks an occupied `BF_CAGE` the jailer may use whose occupant is not down, served and not an enemy. For every releaser the
  cage is unlocked and status `+0xea = 1`; then, if the releaser's faction has `+0x40`, the prisoner's entries for the releaser's booking faction and for the
  jail's booking faction are **erased** (`FUN_1408539f0`), the releaser says `EV_PRISONER_FREE_TO_GO`; if the releaser is a player
  (non-`+0x40` player faction), the prisoner says `EV_UNLOCK_MY_CAGE_OR_SHACKLES`. In those two cases the prisoner gets 133
  `GET_OUT_OF_CAGE_LEGIT`. Always: the restraint is freed; "{1} has been released from prison." for a player-faction prisoner of status type 0.
- **Bail** (dialogue action 43 `CHOOSE_PRISONER_BAIL` opens `CharacterTrading_PrisonerBailout` `FUN_1406ab890`, "Pay Bounty", **Observed**): the list
  is the living occupants of `BF_CAGE` furniture in the speaker's home town's buildings that are player-faction, or not hostile to
  the speaker under further tests (`FUN_1405995d0`, `FUN_140599590`, `FUN_14059a920 < 36`). Price `2 * bounty` with a zero faction,
  which through the cage rule of 11.2 is the jail's booking faction; at least 2000 when the prisoner is not of the player's faction.
  Paying (`FUN_1406ae170`) unlocks the cage, erases the jail's entry (`FUN_1408539f0(ledger, 0)`), sets `+0xea = 1`, the prisoner says
  `EV_BOUGHT_ME_FROM_SLAVERY` and gets 133 `GET_OUT_OF_CAGE_LEGIT`. Only two base dialogues carry DA 43 (**Verified** by probe, 2026-10-21): "Shek law
  bounty" (shek bounty desk, the reply "I'm here to bail out a friend.") and "Inquisitor heretic bounty" (Holy Nation Inquisitor); no United Cities law
  dialogue offers bail.
- **Escape**: leaving by 207/256, or being lifted out, gives a pending Escaping Prison (0xc) and the escaped flag ([locks](locks.md#cages-and-beds));
  a pending crime becomes a bounty only when a booking witness sees it (11.2). Spotting the flag: 11.8.
- `FUN_140851d10` (a lerp of 350/600 and 450/80-based values by a health fraction) is a different, undecoded function.

### 11.6 Booking factions and police hits (**Observed**)

- **Faction `+0x40`** (FACTION loader `FUN_1407fe270`): `fundamental type` 1 (LAW_ENFORCEMENT), else `offers bounties`. Base data:
  Police, Mercenary Police, United Cities, The Holy Nation, Shek Kingdom, Republic of Tertius (**Verified** by `CrimeInstallTests`).
  The loader also stores `fundamental type` at `+0x34` and `not real` at `+0x1d0` (the setter's "lawless" tests).
- **Hitting the police** (`FUN_140665fa0`): a landed blow between a police-like and a non-police character gives the non-police one a
  pending Assault (timer 30) against the police faction, booked with it at once, whichever struck.
- **Attack order** (`FUN_140334070`): a pending Assault against the victim's faction unless the victim has a bounty with the attacker's
  faction, its status flag `+0x114` is set, it is a BANDIT, it has a pending crime, or `FUN_140436610(victim, 1)` (undecoded) holds.
- **Murder**: no caller of the setter `FUN_140852c80` (26 callers, plus the 7 of its wrapper `FUN_140852e10`) passes code 4, and no
  variable-code caller can yield 4 (task finders pass 2/7, 3/10; dialogue DA 49 passes 0 to clear). A killing therefore sets **no
  crime**: its consequences are the relation events of [factions 3.5](factions-squads-towns.md#35-what-fires-relation-changes-observed-decompile)
  (kinds 5, 3 and 4) and an Assault pending from the attack order. Code 4 only names a ledger bit (**Observed**).
- **Assault to code 9**: the setter turns a code 5 into 9 when its victim passes `FUN_140796240(victim, 1)` (decompiles as void; **Unknown** test).
- **Witness timing** (`FUN_1408580d0`, the perception's per-seen-character update, **Observed**): the witness function `FUN_140854d10` runs for a
  seen character when the update says it is newly seen (either of two flag arguments), or while it has a pending crime whose timer
  (`+0x180`) is above 5; every run that reacts sets that timer back to 20 (`FUN_140851f40`), so a watched crime stays fresh. A witness
  that regards the culprit as friendly and sees it wrong the witness's own faction costs the culprit's faction 20 relation each time
  ([factions 3.5](factions-squads-towns.md#35-what-fires-relation-changes-observed-decompile)).
- `FUN_140673a20` in the trespass path is the squad-memory tag MY_INTRUDER, not a witness function (factions 3.5).

### 11.7 Arrest

Arrests are started by **dialogue**, not by a crime test in code (**Verified** by probe of the merged data; enum numbers from the FCS
decompile's `DialogActionEnum` / `DialogConditionEnum` / `EventTriggerEnum`, compare-by 0 `==`, 1 `<`, 2 `>`).

- **Triggers**: law dialogue packages ("police", "Npc MILITARY Law 3", "... Holy Nation Law 3", "Npc TOUGH Vigilante Law 3", gate and city
  guards) answer events 8 `THIEF_CAUGHT_STEALING_FROM_ME`, 41 `WITNESS_GENERIC_ASSAULT`, 43 `WITNESS_THIEF_OR_LOCKPICK`, 44 `BOUNTY_SPOTTED`,
  45 `ESCAPED_PRISONER_SPOTTED`, 40 `ESCAPED_EX_SLAVE_SPOTTED`, 15 `I_DEFEATED_SQUAD` ("Law enforcement defeats squad (arrest them)",
  gated by `DC_CARRYING_BOUNTY_ALIVE` false) and 1 `PLAYER_TALK_TO_ME` with lines carrying `DA_ARREST_TARGET` (29). Bounty dialogues
  branch on `DC_BOUNTY_AMOUNT_ACTUAL`: "Small bounty" (0 < b < 1050) offers a fine; refusing or lacking money runs `DA_ARREST_TARGET`;
  "small fry" (1050 < b < 25000) and "super high" (> 25000) arrest at once. "Surrender" exists only as these dialogue choices; there is no
  surrender state.
- **How the fine is reached** (**Verified** by probe of the base load order, 2026-10-21): "Small bounty" is an event-44 monologue (bubbles)
  for a *leader* (`DC_IS_LEADER` on the speaker); its "I knew it, you're a convict!" line runs `DA_TALK_TO_LEADER` (2: the speaker squad's
  leader, here itself, gets task 102 and walks up to fire event 1, `FUN_140680560` case 2, **Observed**) and sets `interrupt` "Bounty
  extortion pay", so that talk opens the fine window. No law package answers event 1 with bounty lines. The other event-44 law roots
  ("police bounty spotted" b > 500 within talking range, "small fry", "super high", Shek "Shek law spots bounty") say a bubble whose lines
  run DA 29 (police add DA 33); event 45 roots ("police escaping prisoner", "Law enforcement spots prisoner escaping") likewise.
- **Fine** (DIALOGUE "Bounty extortion pay", `Npc MILITARY Law 3`; **Verified** by probe of the base load order): one line per bracket,
  `250 < b < 351` -> 400, then +100 per 100 up to `950 < b < 1051` -> 1100; the 300 line (`0 < b < 251`) tests the *speaker's* bounty
  (`who` 0, a data slip: it never shows for a guard without a bounty). Paying needs `DC_PLAYERMONEY > fine` (strict) and runs
  `DA_TAKE_MONEY` (8, the fine) + `DA_CLEAR_BOUNTY` (49); the 1100 line also runs `DA_PAY_BOUNTY` (10). 49 erases two entries, the speaker's booking faction's and the target's town's (or jailer's) ([dialogue](dialogue.md#effects));
  As built: [simulation](../simulation.md#arrest-and-jail).
- **`DA_ARREST_TARGET`** (`FUN_140680560` case 0x1d, **Observed**): when the speaker's ownership town (`Character+0x658 -> +0x30`) is set
  and equals the speaker's status `+0x88` (read as "he is in his own town"), and the target is not an animal (slot `0x248` is `asAnimal`): the speaker gets tasks 240 `LIFT_PERSON_SNATCHING_ALLOWED_IN_TOWN_ONLY`
  ("Capture Target", needs `isTargetDown`) and 112 `FIND_CAGE_AND_PUT_IN` on the target, and the target's squad marks the speaker
  (flag 5). Otherwise the speaker's squad marks the target (flag 4) and the speaker attacks it (task 4 `MELEE_ATTACK`). Either way the
  guard fights until the target is down, then carries it. `DA_ARREST_TARGETS_CARRIED_PERSON` (30, case 0x1e) does the same for the body
  the target carries (it is dropped and taken over). `DA_ASSIGN_BOUNTY` (32) adds its value as a bounty with the speaker's booking
  faction, or books the pending crime when the value is 0.
- **Arrest goal** (AI_TASK "Arrest bounties", enum 204 `HUNT_BOUNTIES`, in "addon- law enforcement" and the "Gate guard ... -arrests"
  packages; **Verified** by probe): desire 1 when the cage finder finds a free cage, else 0 (AI slot 33 `FUN_140598a90`); target finder
  `FUN_14099bfc0`: the first sensed character (mask 0x400e/0x221) not carried, not of a `not real` faction, not an animal (slot `0x248`), not
  imprisoned, that has a perceived bounty with the guard (`FUN_140853fd0`: the entry for the guard's booking faction, visible while
  `+0x110 > 0`, or when the target is down and an enemy), or that the guard's squad marks with flag 7, or regards as an enemy. Its effect
  129 leads the planner to 203 Arresting (list B: target down, unarmed, carried) and then the cage goals.
- **Caging** (goals 112 / 281 "Find and put in cage", subtask 108 `PUT_IN_CAGE`): desire `FUN_1405a1140` (0 unless carrying;
  0.1 / 0.5 / 1.4 / 2.0 by status, death and bounty tests in `FUN_1405a0f10`); cage finder `FUN_1408d0770(ai, wantFree 1, function 8)`
  over the buildings the AI knows (interiors included): usable (`FUN_140621230`), not reserved, free; weight
  `clamp(1 - d^2 / 4608^2, 1e-5, 1)`, x100 if the cage is of the carrier's faction (otherwise it must be usable), x100 if in the
  carrier's home, x1e-5 for a police carrier when the cage's building fails slot 0x290; the **highest** weight wins (**Observed**).
  No free cage: the finder returns nothing; what the carrier then does is **Unknown**.

### 11.8 Bounty spotting and bounty hunters

- **Spotting** (`FUN_140856f00`, **Observed**): a non-player looker within 400 units (160000 squared) recognises a player-faction
  character whose bounty with the looker's faction is at least a class threshold (`FUN_1408520c0`: LAW_ENFORCEMENT 1, MILITARY 4000
  or 2000, ADVENTURER 1000, SLAVE never, others 10000), immediately while `+0x110 > 0`, else through a recognition meter
  (`FUN_140856cb0`, fires above 1.0; rate `5 + 40 * k`, `k` a 1..2 value from the target's stats scaled by status factors 0.5 / 0.6 /
  0.75 and x2, not decoded); not while the target is carried or imprisoned. Recognition fires `EV_BOUNTY_SPOTTED` (44). The same function
  handles escaped slaves (events 36 / 40). `EV_ESCAPED_PRISONER_SPOTTED` (45) fires in `FUN_140859680` for a target with the
  escaped flag, seen by a police-like looker (or one of a bandit town's faction) in his own town.
- **Bounty hunters** (**Verified** by probe): squads "Bounty hunters wandering between towns", "Bounty hunters low", "Bounty Hunter
  Solo Shek", rebirth "Bounty hunters" (faction Bounty Hunters, `legal system` United Cities) run "hang out in a bar" / "Wandering
  from town to town" / "Patrol map (short range) LOOTING", none of which has goal 204 or a cage goal. Their dialogue answers
  event 44 for bounties > 4000 (two dialogues, split at 35000) with `DA_ATTACK_CHASE_FOREVER` (6), never `DA_ARREST_TARGET`.
  "Bounty Hunter" is NPC class 9 (ADVENTURER), "Bounty Hunter Low" class 1 (police-like). Whether a hunter delivers a captive
  anywhere is **Unknown** (nothing in its data does). Bounty Hunters is the only base FACTION of `fundamental type` ADVENTURER with a
  `legal system` (**Verified** by probe; the others with one: Medics Guild, Traders Guild, Slave Traders, Free Traders, Tech Hunters,
  Empire Peasants, Machinists -> United Cities; the empires point at themselves).

### 12. Slaves, prisoners, animals

The slave value `buyValue` (`FUN_1406751f0`, **Observed**; [expression reference](#expression-reference)): `S` = `CharStatsAnimal::vfunc_2`'s value (the strength stat
`+0x80` times `k`), `attack` = the melee-attack stat (`+0x120`), `D` = the dexterity stat (`+0x88`), `k` = `CharStats+0x34` (1 for people, an animal's age), stat offsets
from [character-stats.md](character-stats.md). RACE `buy value` is 100 for most races (27 of 53 records); other values in the data: 400 x8, 10 x4, 50 x3, 75 x2, 200 x2,
250 x2, 1000 x2, 0 x2, 300 x1 (**Verified** by probe). fcs.def gives it default 100, "percentage of normal value when bought or sold as a slave" (**Verified**); whether
the exe scales it at load is **Unknown** (the formula is read as multiplying the stored value).

| Window | Class | Price (**Observed**) |
| --- | --- | --- |
| Selling a slave to a slaver | `CharacterTrading_SlaveSelling` `FUN_1406a6f50` | an explicit override if the caller gave one, else `clamp(buyValue / 2, 200, 2500)` |
| Buying a slave | `CharacterTrading_SlaveBuying` `FUN_1406a7970` | override or `buyValue`; **doubled** when the character is of the player's faction |
| Buying an animal | `CharacterTrading_AnimalBuying` `FUN_1406a9440` | override or 14000, times `(age + 0.1)`, the age the animal's `vfunc 0x398`; list and confirm in [animals](animals.md#buying-animals) |
| Paying bail | `CharacterTrading_PrisonerBailout` | section 11.5 |

The override is the dialogue effect's val0 when it is 1 or more. **Slave windows** (DA 41 `CHOOSE_SLAVES_SELLING` / 42 `CHOOSE_SLAVES_BUYING`, `FUN_140680560`,
**Observed**): the slaver is the dialogue's speaker, the customer its target (the player character); nothing happens when they are the same. Both open the
`CharacterTradingWindow` (`FUN_14072a880`, one at a time: opening one closes the previous; layout in [ui-screens 6](ui-screens.md#6-inventory-trade-dialogue-build)) with a handler
holding the slaver, the customer, the character list and val0.
- **Selling** (41, title "Selling Slaves", confirm "Sell"): the members of the customer's squad that are not dead and within 300 units (3D) of the slaver, the customer
  included; no slave or prisoner test. A row's first panel is ("Name:", name) and ("WARNING: Character will become a slave!", ""), the pair added 5 times in a loop (a quirk;
  Meitou shows it once); the second is empty. Confirm (`FUN_1406a7740`), each ticked character: made the slaver's slave (slave state 1, "{1} has been enslaved" for a free
  player-faction character, the owner set, AI order 187 obedient slave toward the owner, SHACKLES made and worn when it wears no locked armour); its faction and squad are
  kept (it stays in the player's squad, uncaged); its squad remembers the customer with long-term tag 5 MY_CAPTOR; relations toward the player's faction: the slaver's +4,
  its own -4. Then the total is **added** to the player faction's money; the slaver's money is not reduced.
- **Buying** (42, title "Buying Slaves", confirm "Buy"): every character in the slaver's owned set that is a slave (state 1) and not dead; no radius, cage or count limit
  (the set is per owner object, **Inferred**). Panels: Strength, Dexterity, Toughness, Melee Attack, Melee Defence | Labouring, Farming, Cooking, Engineering, Medic (stat
  values). Confirm (`FUN_1406add20`), each ticked character not dead: its price added to the total; no longer owned; split from a squad of more than one into a new squad of
  its own faction (it does not join the player); event 51 BOUGHT_ME_FROM_SLAVERY at it with the customer as target; its cage unlocked; slave state 0; it leaves its cage or
  restraint; AI reset; its locked shackles unlocked (still worn); its squad remembers the customer with tag 3 FREED_ME; task 133 GET_OUT_OF_CAGE_LEGIT; relations toward the
  player's faction: the slaver's +1, its own +4. Then the total is taken from the player faction's money (refused without deduction when it has less; the result is ignored);
  nothing is paid to the slaver.
- The window's gate (`FUN_1407281c0`) makes such a shortfall unreachable: confirm is enabled only with a row ticked and the total within the buyer's money (the slaver's when
  selling, the player's when buying).

Meitou's reading: [simulation, slave trading](../simulation.md#arrest-and-jail).

## Unknowns

- **The stolen predicate** `FUN_14079e550(item, 1)`: it reads two owner handles (`+0x140`, `+0x160`), a flag byte, and a type
  check against the type list `{2, 3, 4, 0x2e, 0x56, 0x66, 0x6b, 0x6f}`. What exactly makes an item "stolen" (the thief, the
  owning faction vs. the holder's, expiry) and the second halving condition in `FUN_140793490` are not decoded. The `0.5`
  stolen factor in `T` is therefore only roughly defined. *Partly resolved 2026-10-13* ([carry-loot.md 4.4](carry-loot.md#stolen-ownership)):
  `+0x140` is the owner the item was taken from, `+0x160` its current holder (a backpack's owner for a backpack); stolen = the from-owner is set,
  differs from the holder and is of another faction, unless the holder's faction is lawless; the type list is the lootable item kinds. Expiry and
  the halving condition remain open.
- **The meaning of `Weapon/Armour+0x208`** (inferred as "crafted by the player" from the editor text of the two player rows; not shown by a writer).
  `Armour+0x1e8` is the armour's `uniform` faction (**Observed**: `Gear::vfunc_76` reads the item state's `uniform` string into it; the
  recruit join clears it), so a uniform sells at 0.3x. Meitou does not track uniforms yet.
- **Whether the town being set up is already in the global town list `DAT_142134100` when its own market factors are rolled.**
  If it were, its distance to itself is 0, the spread `w` is 0 and its own `p` is 1.0, which would force every factor to 1.0 and
  make the feature inert; so either the town is added after setup or something not yet seen intervenes. The order of the list
  decides which neighbours have factors already.
- **Whether `Zones_TownSetup` runs on a save load** and, if so, whether the saved `trade goods` factors (section 7.2, saved by `FUN_140376b80`,
  loaded by `FUN_1403715c0`) overwrite or are overwritten by a fresh roll. (That the factors are saved is settled.)
- **What `FUN_140745e30` really does** (it forwards a money delta to virtual slot 54 of the object at `+0x80`; the purse mutation itself) and how
  the trade window decides the `k` units priced with the opposite flag (`FUN_14070e3b0`, section 5).
- **Where `MapItem+0x1e8` (the map price) comes from**, and which record `BlueprintItem+0xc0` points at (section 10).
- **How weapon quality is rolled for shop stock** and how VENDOR_LIST reference value 1 (and a value 0 in the first slot, as in
  `(0,100,0)`) is read for clothing, armour and weapon entries (`FUN_140956ba0` / `FUN_1409555d0` read them; "val0 is relative
  chance" per the editor, but 472 entries have (0, 100, 0)). It follows the NPC clothing rule in [characters.md](../characters.md)
  that quantity 0 never spawns, but this was not re-verified for shops.
- **The artifact placement** (`ArtifactTown`, which squad carries which artifact) beyond the six field readers.
- **How a pending crime's timer counts down** (`+0x90`; set by `FUN_140851f40` / `FUN_140852e10`, the decrement was not found), and so how long an unseen crime stays pending.
- **Whether CHARACTER `money min` / `money max`** exist in the data and are unused (the editor lists `money`; not found
  being read for NPC purses).
- **What `FUN_140851d10` computes** (`prison time`'s consumer is the cage sentence, 11.5).
- **Arrest and jail gaps** (11.5, 11.7, 11.8): what a guard carrying a captive does when no cage is free (`CAGES_ARE_ALL_FULL` 97 is
  registered but no TaskData row uses it); how `+0x110` decays; the escaped flag's expiry ("Leave town to clear this status. Expires in
  {1}", cleared by `FUN_1404ff060` for an undecoded reason);
  whether NPC bounty hunters ever cage or hand in a captive; what the speaker-in-own-town branch of `DA_ARREST_TARGET` tests exactly.
- **Research cost** beyond the formula of section 10, crafting costs, building purchase price, bed cost.
- **The purpose of `FUN_1405d4260`** (the crime-in-progress tooltip function; its behaviour remains untraced).
- **Which item records make a `MoneyItem`** (priced at their `value` alone, section 2): `Cats`, and probably `String of Cats` and the other value-only items, but the constructor's
  choice of class was not traced; Meitou tests the name `Cats` (found 2026-10-14 while building the trade).
- **The names of the VENDOR_LIST reference lists** (`items`, `weapons`, `clothing`, ... in section 9.2) and the reading of a v0 of 0 in them: Meitou reads every list and keeps
  each entry by its target's record type, with 0 counting as 100 (engine choices, [../simulation.md "Trade"](../simulation.md#trade)).
- **The FACTION field names of the armour vendor quality chances** (`armors 0..5` in section 9.2): read as the ints `armors 0` .. `armors 5`, falling back to the editor defaults; not
  checked against the data.
- **Opening a trade**: Meitou opens it within 30 units; the original's opening distance is not traced (the item check is in [Trading range](#trading-range)).

## Expression reference

- Item base value: `ItemValue` (`FUN_140896e20`): `lerp(t, a, b) = a + (b - a) * t`; `ItemValue` (`FUN_140896e20`): `s = quality / 100`
- WEAPON: `v = value`; `v = trunc(v * price mod)`; `L(s) = 1 / (1 + e^(-(2s - 1) * 5 * 0.8))`; `raw = trunc( lerp(L(s), 15, 160) * v )`; `result = trunc( raw * sword price mult * global price mult )`
- ARMOUR and clothing: `t = s * s`; `lo = classLo[class] * matLo[material type]`; `hi = classHi[class] * matHi[material type]`; `v = trunc( lerp(t, lo, hi) )`; `v = trunc( v * armor price mult )`; `v = trunc( v * relative price mult )`
- CROSSBOW and LIMB_REPLACEMENT: `raw = trunc( lerp(s*s, value, value 1) )`
- Runtime unit price: Item virtual slots 81 and 82: `(sell != 0) + 3`; `u = ItemValue(record, item quality, 1)`; `u = trunc( u * marketFactor )`; `u = trunc( u * loot price mult )`; `u = trunc( u * charges / max charges )`; `n == 1`
- Trader factor `T` and the stolen test: `T = buyMult * (0.5 if stolen-to-the-player-faction) * culturePrice(item)`; `+0x250 == 0`
- Market factor: `f = uniform in [1 - m, 1 + m)`; `m = trade profit margins`; `w = d / 1,000,000 * m * 10`; `m = 0.3`; `p = 1.0`; `v0 = trunc(factor * 10000)`
- Buying, selling, fencing: `{1} = 100 - round(100 * p)`; `p = lerp(thievery / 100, 0.4, 1.25)`
- Stock size and money (`FUN_14095ab10`, squad shops; `FUN_14095b210`, building shops; **Observed**, decompile and: `fill = vendors fill total amount`; `wealth = clamp(FUN_1402dd530(faction), 0.2, 1.0)`; `n = round( uniform(0.8, 1.2) * fill * wealth )`; `money = trunc( clamp(0.25 * stockValue, 3000, 25000) * wealth )`; `money = trunc( (vendor money + r) * wealth )`
- Restock: `+0x58 == 0`
- Pending crimes, witnesses and the cage sentence: `+0x34 == 8`
- Slaves, prisoners, animals: `buyValue = clamp( trunc( S + attack + D * k ) * RACE buy value, 1000, 20000 )`

## Buyback during an open trade

**Observed** (reported by the PR #41 contributor from the original game; not checked by the owner): an item bought and returned before the trade closes refunds its
purchase price; one sold and bought back costs what the sale paid; closing the trade ends this. The original's item rows have a `Buyback` row right after the name
(**Observed**, decompile, [item rows](ui-screens.md#tooltips)); when it shows is **Unknown**, as is how partially charged or mixed-price stacks are allocated.
Meitou: [simulation](../simulation.md#trade).

## Evidence index

Evidence scopes below apply only to their named sources/checks.

- 4. Item base value: `ItemValue` (`FUN_140896e20`)
- 4.4 CONTAINER (type 0x2e = 46)
- 4.7 WEAPON_MANUFACTURER `price mod` (data, Verified by probe)
- 6. Trader factor `T` (`FUN_14079e6d0`) and the stolen test
- 7. Towns: culture and the market factor (`Zones_TownSetup` `FUN_1409353c0`, `read_forbidden_items_n5` `FUN_1407fd9a0`)
- 9.1 SQUAD_TEMPLATE fields (data = **Verified** by probe over 959 templates)
- 9.2 Stock size and money (`FUN_14095ab10`, squad shops; `FUN_14095b210`, building shops; **Observed**, decompile and
- 9.3 Restock (**Observed**)
- 11.4-11.8 Claiming, sentence, release, bail, arrest, spotting: code **Observed** (decompile); dialogue, AI_PACKAGE and squad facts **Verified** by a probe over the merged base-game data (DIALOG_ACTION `action name`, DIALOGUE_PACKAGE `dialogs` event value, AI_TASK `enum`)
