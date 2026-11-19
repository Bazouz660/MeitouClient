# 02. Players and factions

From one `World.Player` to many players, each with squads, in factions that have relations. Upstream `a1ef2e06f`.
`Sim/` = `src/Meitou.Simulation/`, `Game/` = `src/Meitou.Game/`, `Data/` = `src/Meitou.Data/`.

## Today (facts)

- One `PlayerState` (`Sim/World.cs:437`, `Sim/Player/PlayerState.cs:15`); no player id type exists anywhere. One `World.Research`
  (`World.cs:552`). One open trade (`Sim/Trading/TradeSystem.cs:291-307`), one conversation (`Sim/Dialogue/DialogueSystem.cs:184-200`).
- The player faction is the FACTION record `204-gamedata.base` (`Data/Gameplay/FactionData.cs:74`), one index
  (`FactionRelations.PlayerFaction`, `Data/Gameplay/FactionRelations.cs:67`; `PopulationData.PlayerFaction`, `Sim/Population/PopulationData.cs:373`).
- `FactionRelations` is a fixed n x n table built from the FACTION records, every array `readonly` and sized once (:28-52, :245, :307,
  :343). No faction can be added. It lives in `PopulationData.Relations`, shared by every world built on the same data
  (`PopulationData.cs:374-380`). It is also the live table: `Event`, `Add`, `DeclareWar`, `EndWar` change it (:156-272).
- Relations with the player are one-directional: `Get(player, b)` returns the *other* side's value (:144); the player's own row is never
  read (:9-10). `_rank` is "the player's rank in each faction", one int per faction (:343).
- `CharacterCold.IsPlayer` is a stored flag (`Sim/CharacterTable.cs:292`), set at spawn, load and recruit, cleared at dismissal
  (`Sim/Dialogue/DialogueSystem.Recruit.cs:150`; `Sim/Factions/SquadManagement.cs:649`). It means "a player's character: selectable,
  orderable", not "this player's".
- Ownership is already by faction index: `BuildSite.Owner`, `PlayerTown.Owner` (`Sim/Construction/BuildingRegistry.cs:98,254`),
  `Fixture.Owner` (`Sim/Locks/Fixtures.cs:289`), town property `Owner` plus a `Purchased` flag (`Sim/Locks/FixtureRegistry.Property.cs:43-47`).
- Bounties are already per character (`CharacterCold.Bounties`, `CharacterTable.cs:1028`). A squad has its own purse option
  (`Squad.OwnPurse`, `Sim/Factions/Squads.cs:212`).
- `FactionDeeds` already applies relation events "for any two factions" (`Sim/Factions/Relations.cs:161-166`).

How often the singleton is used in `src` (fixed-string grep; `Player.Faction` includes 5 `FactionName`, `Player.Squad` 17 `SquadAi`):

| Pattern | Simulation | Game | Meaning for the migration |
|---|---|---|---|
| `Player.Faction` | 276 in 80 files | 33 in 16 | "the player faction": must become the faction of *someone* |
| `.IsPlayer` | 224 in 84 | 77 in 25 | mostly stays ("any player's character"); needs the owner where paired with the line above |
| `PlayerFaction` (data level) | 147 in 42 | 18 in 12 (+18 in Data) | the record's index: stays as "the primary player faction" |
| `Player.Selection` | 37 in 23 | 9 in 5 | per player |
| `Player.Money` | 43 in 17 | 5 in 3 | per player |
| `Player.Squad` | 16 in ~14 | 3 in 2 | per player |

`Player.Faction` by Simulation folder: Locks 41, Ai 38, Dialogue 33, Construction 18, Population 18, Kidnap 17, Production 16, Saving 13,
Crime 12, root 10, Factions 10, Player 10, Aggro 8, Bodies 8, Perception 6, Trading 5, Combat 4, Items 4, Carrying 4, Towns 1.

## Target (proposal)

### Three records instead of one

| Record | One per | Holds (from today's `PlayerState`, lines in `Sim/Player/PlayerState.cs`) |
|---|---|---|
| `Player` | account on this server | `Id` (a small int, stable in the save), account key, name, `Faction`, online flag, last seen, view anchor |
| `PlayerState` (kept name) | player | `Selection` :115, `Squad` (active) :122, `Inspected` :128, `Pointed` :135, `InspectedBuilding` :148, `InventoryShown` :153, open container/loot/door and their walkers :200-247, `Held` :253, `HeldSerial`, `InventoriesClosed`, `Money` :39, `SquadAi` :45, `InCreation` :185, cheat flags :61-101 (admins only) |
| `FactionState` | player faction | `FactionName` :34, `Towns`/`Camps` :275-300, `KnownFactions` :319, `SeeDiscoveries` :334, research (today `World.Research`), rank in other factions (today `_rank`), members and roles |

`World.Players` holds the players; `World.PlayerFactions` the faction states. `BanditsLoot` :51 is a new-game option and becomes a rule
of the server ([07](07-settings-mods-saves.md)).

Decisions:
- **Purse per player.** Kenshi has one wallet per faction; with several people in one faction a shared wallet lets anyone spend
  everything. Each player has a purse; `GiveMoney(player, amount)` moves money. A faction may turn on a shared purse later
  ([09](09-open-questions.md), Q5).
- **Research, map knowledge and relations per faction.** Members share them, as they share a base.
- **Selection stays on the server.** Orders with an empty id list resolve against it (`Sim/Movement/MovementSystem.cs:251`), it is
  hashed and saved. The client shows its own selection at once and the server's copy follows ([03](03-commands-and-authority.md#latency)).

### Who owns a squad

`Squad.Owner` (a `Player.Id`, -1 for none) is new. Rules:
- A character is a player's when its squad's owner is that player. `IsPlayer` stays true for every player's characters.
- Your squad lists show the squads you own (`SquadManagement.Mine`, `SquadManagement.cs:394`, gains the owner test). Another player's
  squads, in your faction or not, are not in them. They are characters in the world like NPCs, shown with their owner's name.
- Several squads per player work as today (`MaxSquads`, `MaxSquadSize`, `SquadManagement.cs:202,237`). The limits become per player;
  the rule `players.maxCharacters` caps a player's total ([07](07-settings-mods-saves.md)).
- Faction mates may heal, carry, feed and trade with each other's characters as allies do. They may not order them. A player may hand a
  character or a squad to a faction mate (`TransferCommand`, the receiver accepts).
- Recruiting (`DialogueSystem.Recruit.cs:54-150`) joins the recruit to the *speaking character's owner's* active squad and faction.
- An independent squad (`Squad.Independent`, `Squads.cs:185`) and a dormant one (`:223`) keep their owner.

### Player factions as real factions

The faction list must grow at runtime, and two player factions need a relation with each other.

- **A world-owned table.** `FactionTable` (proposal, in Simulation) wraps the data's `FactionRelations` and adds runtime factions after
  the data's n. Indices 0..n-1 are unchanged, so every hashed faction index of today is unchanged. A new player faction gets index n+k
  and a `FactionData` cloned from the player record with the id `meitou-pf-<number>`, so every reader of faction fields keeps working.
  The table belongs to the world, not to `PopulationData`: two worlds on the same data (the title battle, tests) no longer share
  mutated relations. **Risk:** `PopulationData.Factions` is `required init` (`PopulationData.cs:358`) and many readers index it; they must
  read through the table. This is the deepest change of the design.
- **`IsPlayerFaction(index)`** replaces `== PlayerFaction` in `FactionRelations` (:144, :160, :190, :230, :255, :272, :288). For an NPC
  faction a and a player faction p the value stays where it is today, in a's row at column p. Each player faction gets its own column,
  so each has its own standing with every NPC faction. A new faction starts from the same rule as the original's
  (:113-115: the NPC faction's explicit entry for the player record, else the smaller default).
- **Player to player:** a small symmetric table `Diplomacy[p, q]` with three states. Its value answers `Get(p, q)` as 100, 0 or -100, so
  `AggroSystem.IsEnemy`, `IsAlly` and every relation reader work unchanged.

| Change | Who decides | Notes |
|---|---|---|
| Neutral to hostile | either side, at once | Declared, with a message to both. A rule can forbid it (`pvp: off`) |
| Hostile to neutral | both accept | |
| Neutral to allied | both accept | Allies see each other on the map and share `KnownFactions` meetings; nothing else |
| Allied to neutral | either side, after a notice period (rule, default 10 game minutes) | Stops a "betray inside the gate" in one tick |
| Blows between non-hostile player factions | rule `friendlyFire`: `off` (default: such blows do no damage), `on`, or `turnsHostile` | `FactionDeeds` (blows change relations) is skipped for player-player pairs; diplomacy is explicit |

- **Joining a faction.** `RequestJoin(faction)`; a member with the invite right accepts. The joiner's squads keep their owner, change
  faction index; the joiner's buildings change owner to the new faction; its old faction, if empty, is retired (its relations and
  research are dropped, its index is not reused until restart). Bounties stay on the characters. Leaving: the player gets a fresh
  faction with default relations and keeps its squads; buildings stay with the faction left.
- **Roles** in a faction: leader (the founder), officer (invite, diplomacy, building rights), member. Kept minimal.

### Crimes and bounties

Facts: bail, fines and bounty payouts read and write `world.Player.Money` and compare with `world.Player.Faction`
(`Sim/Crime/ArrestSystem.Bounties.cs:144-257,469-537`); pending crimes are booked "from the player's deeds".
Proposal: the money is the purse of the *owner of the character involved* (who is bailed, who hands in the bounty); the faction is that
character's. A crime against a player faction's property is a crime as against any faction's: `Fixture.Owner` already tells whose it is.
Whether guards of an NPC town defend one player's property against another follows the town faction's relation to each, as for NPCs.

### Bases and ownership

- `ConstructionSystem` uses `world.Player.Faction` at 15 places (`Sim/Construction/ConstructionSystem.cs:285-878,1522`); each becomes the
  sending player's faction. `AddTown(mean, faction)` already takes the owner (:878).
- Build rights: any member may build in the faction's outposts unless the leader restricts it.
- `FixtureRegistry.OwnedProperties` caches one faction (`_ownedFaction`, `FixtureRegistry.Property.cs:121`): becomes a cache per faction.
- A player town's presence test, name, level and radius are already per town (`BuildingRegistry.cs:219-254`).
- Unattended and offline bases: [07](07-settings-mods-saves.md#offline-players-and-bases).

### Raids

Facts: each NPC faction's war manager rolls every 6 game hours when it has no campaign (`Sim/Population/PopulationCampaigns.cs:277,428-447`);
`Roll` aims at one enemy, `PlayerFactionOf(world)` (:307, :558-570); a candidate player town counts only while an `IsPlayer` character
stands in it (`TownActive` :732-754; the "within 1250" of docs/simulation.md:530 is the town radius, there is no such constant in the
file); the chance uses the number of living player characters (:600-609).

Proposal: candidates are the towns of **every** player faction; "stands in it" and the living count are those of the target town's
faction. The manager still runs one campaign at a time per NPC faction, so the number of raids in the world does not grow with the number
of players; on a large server that makes raids rare per player, and a rule (`raids.perFaction`) can give each (NPC faction, player
faction) pair its own manager later. An offline faction's town is never a candidate, because its characters are not in the world
([07](07-settings-mods-saves.md#offline-players-and-bases)). Messages that say "your town" go to the target faction's members.

### Things that are per player for a subtle reason

| Thing | Today | Becomes |
|---|---|---|
| Unique characters' "player flag" (docs/simulation.md:384-392) | killed or caged by "the player" | records the faction; world states that ask "did the player kill X" are true if **any** player faction did (one shared world story) |
| Discovery (`PopulationSystem.Discover`, `Sim/Population/PopulationSystem.cs:397-468`) | viewers are all player characters plus the one focus, results to `world.Player` | per faction: its members' characters and anchors, results to its `FactionState` |
| Tutorial facts (`WorldSnapshot.TutorialFacts`) | one | per player |
| Nests do not spawn near "a player character" (docs/simulation.md:502-504) | any `IsPlayer` | unchanged: any player's character |
| Self-defence, Hold, jobs of player characters | `IsPlayer` | unchanged |

## Migration path

Each step leaves single-player behaviour and every `GoldenHashTests` pin unchanged. How the pins survive: `StateHash` hashes
`PlayerState` unconditionally as `Faction`, `Money`, `Squad`, `Selection` (`PlayerState.cs:348-370`). Player 0's state is hashed in
exactly that place and order; further players, owners and runtime factions are hashed with a tag only when present, which is the code's
existing convention ("so worlds without ... hash as before", `Sim/World.cs:1233-1251`). The data's faction indices never move.

**Stage 1: several players, one faction** (packages 9-11 and 19 of [08](08-milestones.md)).
1. Add `PlayerId`, `Player`, `World.Players` with one entry. `World.Player` stays as a property returning player 0's state. No caller changes.
2. Add `Squad.Owner`, set to player 0 wherever a player squad is made (spawn, load, recruit, new squad). Not yet read.
3. Move per-player sessions (trade, dialogue, containers, held item) from single fields to a map keyed by player. One player: one entry.
4. Give the per-player uses the acting player: `Player.Selection` (37), `Player.Money` (43), `Player.Squad` (16) in Simulation become
   `world.Players.Of(command.Sender)` or `world.Players.OwnerOf(character)`. With one player both return player 0.
5. `SquadManagement.Mine` and the snapshot's squad lists filter by owner.
After stage 1, friends play in one faction.

**Stage 2: several player factions** (packages 22-27).
6. Introduce `FactionTable`; route `PopulationData.Relations` and faction lookups through it; no runtime faction yet.
7. Replace `world.Player.Faction` folder by folder, largest first (Locks, Ai, Dialogue, Construction, Population, Kidnap, Production,
   Crime, then the rest). Each site gets one of three helpers, chosen by reading the site:

   | The site means | Helper | With one faction returns |
   |---|---|---|
   | the faction of the character at hand | `c.Faction` (already there) or `world.Players.FactionOf(id)` | the player faction |
   | "is this a player's faction" | `world.Factions.IsPlayerFaction(f)` | `f == Player.Faction` |
   | the faction of whoever sent the command | `world.Players.Of(sender).Faction` | the player faction |

   `AggroSystem.PlayerSide` (`Sim/Aggro/AggroSystem.cs:326-327`) is the model: one helper already serves five call sites.
   One folder per pull request; every pin must hold after each.
8. Split `FactionState` out of `PlayerState`; research and map knowledge move with it.
9. Turn on runtime factions: creation at join, the diplomacy table, joining and leaving, raids over all player factions.
10. Delete `World.Player`.

The Game side (33 + 77 places) changes in package 14, when the UI moves to the personal view and stops reading `Player.Faction` at all.
Tests reference the singleton too (`Player.Faction` 44 places in 23 test files): they keep compiling until step 10 and change there.

## Not checked

- Which characters have `IsPlayer` without the player faction, or the reverse (slaves and prisoners in a player squad file are "not the
  player's", `Sim/Saving/SaveCapture.cs:670-673`).
- `UniqueCharacters` and shop code were not re-read for player assumptions.
- What creates player-faction entries in `world.Platoons` (`PopulationSystem.cs:588-594`).
- Start records' relation and research effects are "partly unknown" upstream (docs/simulation.md:575-577); a new faction uses whatever
  the start gives the original's player faction.
