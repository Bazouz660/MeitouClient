## Single-player assumptions in `Meitou.Simulation`: multiplayer readiness

### 1. What `PlayerState` holds (`src/Meitou.Simulation/PlayerState.cs`)

| Member (line) | Hashed (`Hash` 98-109) | Kind | Notes |
|---|---|---|---|
| `Faction` (11) | yes | shared faction | `Exists => Faction >= 0` |
| `Money` (14) | yes | shared faction purse | Spent by trade (TradeSystem.cs:336,375), bail (ArrestSystem.Bounties.cs:98-99), surgery (CharacterEditing.cs:85) and dialogue (DialogueSystem.Effects.cs:117) |
| `Selection` (16) | yes (104-108) | **per-person UI, but it changes the simulation** | Saved as CAMERA `selected_characters` (SaveCapture.cs:672) |
| `Squad`, the active squad (21) | yes (102) | **per-person UI, but it changes the simulation** | Recruits join it (DialogueSystem.Recruit.cs:29) |
| `InCreation`, `CreationRaces` (23,25) | no | session phase, global | Gates `EditKind.Creation` (CharacterEditing.cs:55) |
| `Inspected`, `InspectedBuilding` (27,29) | no | per-person display | BodySystem.cs:82 computes `Extended` for `Inspected` (display only) |
| `OpenContainer`, `ContainerOpener`, `OpeningContainer`, `ContainerWalker` (31-34) | no | per-person, **affects the simulation** | `ContainerWorkers` calls `MovementSystem.Stop` (Items/Containers.cs:72) |
| `DoorTarget`, `DoorWalker` (36-37) | no | per-person, **affects the simulation** | `DoorWalkers` opens and closes doors and stops the walker (Locks/LockSystem.Doors.cs:136-140) |
| `Towns`, `Camps` (42,59) | no | shared map knowledge | The comment says "nothing in the simulation reads it", but `ReadMap` reads `Towns` to decide whether a map item is used up (PlayerState.cs:252-255) |
| `SquadAi` (44) | yes if not default (103) | shared faction | Read by ProductionSystem.Jobs.cs:105 |

Shared faction state outside `PlayerState`:
- `World.Research` (World.cs:141) and `World.Machines` (143).
- Buildings get `Owner = world.Player.Faction` (Construction/ConstructionSystem.cs:231).

Open conversation and trade live inside their systems:
- **Conversation:** `talker`, `target`, `open`, `view` and `request` (Dialogue/DialogueSystem.cs:81-85). It is hashed (line 372).
- **Trade:** `customer`, `trader` and `open` (Trading/TradeSystem.cs:98-101). The doc says "the open trade is not [hashed]" (line 90), yet it decides which customer `BuyItem` and `SellItem` apply to.

**Lockstep risk:** `DoorTarget`/`ContainerWalker`, `Towns` (through `ReadMap`) and the open trade change hashed state but are not hashed themselves. A desync there would not show up in `StateHash` until it spreads.

### 2. How commands resolve "the player", and ownership

**Fallback to the selection.** 12 sites use the pattern `ids.Count > 0 ? ids : world.Player.Selection.ToArray()`:
- MovementSystem.cs:111
- Orders.cs:165,174
- PlayerState.cs:157
- Items/Containers.cs:30
- Items/PickUp.cs:25
- Carrying/CarrySystem.cs:107
- Construction/ConstructionSystem.cs:131
- FirstAid/FirstAidSystem.cs:88
- Locks/LockSystem.cs:26
- Production/ProductionSystem.cs:160
- Crime/ArrestSystem.Bounties.cs:57

The UI depends on it: `Say(new MoveOrder([], target) { Tick = session.World.Tick, Queued = queued });` (Meitou.Game/PlayerInterface.cs:69), plus 10 more empty-list sends in `Meitou.Game`. Commands are stamped with the *current* tick, which leaves no input delay for lockstep.

**Validation is inconsistent, on purpose.** `MoveOrder` (MovementSystem.cs:113-117) and `AttackOrder` (Combat/CombatSystem.cs:133) do **not** check `IsPlayer`, because systems defer these commands for NPCs too:
- `world.Defer(new MoveOrder([customer], ...))` (TradeSystem.cs:136)
- `world.Defer(new Combat.AttackOrder([order.Talker], order.Target) …)` (DialogueSystem.cs:171)

So ownership cannot be enforced inside the handlers. It has to sit at network ingress.

**Commands with no actor at all.** They act on "the" player or "the" open window:
- `BuyItem`, `SellItem`, `ArrangeStock`, `CloseTrade`
- `DialogueChoice(int Index)`, `EndConversation`
- `CloseContainer`, `FinishCreation`
- `QueueResearch`/`CancelResearch`, `QueueCraft`/`CancelCraft`/`RepeatCrafts`
- `PlaceBuildings`/`CancelSite`/`PauseSite`
- `ToggleDoorsOrder`, `ToggleDoorLocksOrder`
- `SquadAiCommand`, `SeenDiscoveriesCommand`, `NewSquadCommand`
- `StartContract(SquadId…)`, all `Debug*`

79 concrete command records in total (the task's "~67" is low).

**Values the client asserts and the simulation trusts:**
- `EditCharacter.Paid` skips the surgery cost (CharacterEditing.cs:21,75).
- `DebugMoney` and `DebugSpawn` (DebugCommands.cs:12,35).

**There is no owner field for a human:**
- `Squad.Faction` is `init`-only (Squads.cs:24); `Platoon.Faction` (Platoons.cs:28).
- `CharacterCold.Faction` (CharacterTable.cs:45) plus `IsPlayer`, documented as "Belongs to the player's faction: can be selected and ordered" (74). It is hashed (435).
- Player squads are `s.Faction == world.Player.Faction && s.PlatoonId < 0` (SquadManagement.cs:51). Ownership checks go through `Own()` (144), which tests faction only.

The natural owner slot is a new `Squad.Owner`. A character's owner then comes from `CharacterCold.SquadId → Squad.Owner`. Move-between-squads, recruit and dismiss already go through `SquadManagement` and `Recruit`, so these are the places to check it.

**Ownership enforcement plan** (this design is mine, not in the code):
- Add `public int Issuer { get; init; }` to the `SimCommand` base (Commands.cs:10-14). An init property breaks no constructor.
- A gate at `CommandQueue.Enqueue` (Commands.cs:41) or at the network receive:
  - expands empty lists against the issuer's own selection;
  - drops character IDs whose squad `Owner != Issuer`;
  - rejects `Debug*` and client-asserted `Paid`;
  - binds window commands (trade, dialogue, container, editor) to the issuer's session.
- Deferred commands get `Issuer = -1` (system) and skip the gate.

### 3. Systems that assume one of each

| Assumption | Where | Effect with two humans |
|---|---|---|
| One conversation | `Conversation? open` (DialogueSystem.cs:83); `TalkOrder` calls `End(world)` first (125-126); `DialogueChoice` has no conversation id (9); one `DialogueRequest` (85) | B's Talk closes A's conversation; choices go to whichever is open |
| One trade | TradeSystem.cs:98-99; `OpenTrade` overwrites `customer`/`trader` (124-125) | B's trade replaces A's |
| One container | PlayerState.cs:31-34; `player.OpenContainer = null` (Containers.cs:52) | Last opener wins |
| One door walk | PlayerState.cs:36-37; `player.DoorTarget = null` (LockSystem.Doors.cs:108) | B's door order cancels A's walk |
| One character editor | `InCreation` is global (PlayerState.cs:23); `FinishCreation` from anyone ends it (193-194). `StartPlayer` overwrites `Faction`, `Money`, `Research`, `Squad` and `Selection` (PopulationSystem.Player.cs:85-92) | No way for a second human to join; one Finish ends creation for everyone |
| One selection and active squad | PlayerState.cs:16,21. `Prune` re-selects the first living `IsPlayer` character of *anyone* (88-93); `Dismiss` removes from the single selection (SquadManagement.cs:244) | Selections clash; the fallback can select another human's character |
| One camera focus | `Vector3? focus` (PopulationSystem.cs:38). Zones use it only when there are no player characters (210). **Campaign announcements depend on the camera** (PopulationCampaigns.cs:976) | Several cameras fight over one `FocusCommand`, and that changes the simulation |
| One message stream | `World.Post(string)` has no recipient (World.cs:268) | "Not enough money" and similar messages go to everyone |
| Modal windows pause the game | `RequestedPause = settingsOpen \|\| faithfulScreens.HasModal` (Meitou.Game/PlayerInterface.Faithful.cs:17). `WorldSession.cs:88` then runs `ApplyPausedInterfaceCommands` | One player's dialogue pauses everyone |
| Commands applied off the tick grid | `World.Interface.cs:9-11` applies Select, Buy, Sell, `DialogueChoice`, `EditCharacter` and `PlaceBuildings` between ticks without advancing `Tick` | Changes hashed `Money`, inventories and `Selection`; lockstep has to put these on the tick grid |

Zone activation already handles players far apart: each living `IsPlayer` character adds a focus (PopulationSystem.cs:204-210). Discovery is driven by all `IsPlayer` characters into the one `PlayerState` (172-181).

### 4. How the player faction is represented

**Where it is defined:**
- The data's "Nameless" faction: `factions.FindIndex(f => f.Name == "Nameless")` and `relations.PlayerFaction = player` (PopulationData.cs:174-176).
- `FactionRelations` special-cases one `PlayerFaction` int (Meitou.Data/Gameplay/FactionData.cs:155,169,182,212,237). The player's row is answered from the other faction's table: `if (a == PlayerFaction && a >= 0) return a == b ? 100 : relation[b * Count + a];`.
- The table is a fixed n×n array built once (`Build`, 240+), so factions cannot be added at runtime.
- Relations live in `PopulationData`, shared by every world built on that data (PopulationData.cs:94-96), so this mutable state sits outside `World`.

**Two parallel notions of "the player faction":**
- `world.Player.Faction` is used at about 45 sites.
- `data.PlayerFaction` is a constant.
- Both are compared together at PopulationOffscreen.cs:35 and TradeSystem.cs:208.
- `AiSystem.IsPlayers` returns true for `squad.Faction == data.PlayerFaction` (Ai/AiSystem.cs:740), so the AI never runs those squads.
- `CharacterAssembly.cs:133,142,161` also uses the constant.

**`IsPlayer` is used everywhere:** 164 uses in 53 files under `Meitou.Simulation`, and 24 in the host.
- Only non-player characters pick fights (AggroSystem.cs:237,434,451), so player characters never auto-aggro against anyone, other humans included.
- Assault relation events need `victim is { IsPlayer: false }` (CombatSystem.cs:140).

**Co-op (several humans share Nameless)** fits the current model. `IsPlayer` stays "human-controlled faction". Money, research, map knowledge, `SquadAi`, buildings and machines are naturally shared, which matches Kenshi's single purse. What co-op needs:
- `Squad.Owner`;
- a per-human session for the per-person rows in section 1;
- a join path in `StartPlayer` that adds a squad without overwriting faction state.

**PvP (separate factions)** is much larger:
- Turn `FactionRelations.PlayerFaction` into a set, and define relations between two player factions. Today neither player faction has a table, so player-to-player lookups are undefined.
- Add synthetic FACTION entries at `PopulationData.Create` (172-176), because the table cannot grow later.
- Replace each `world.Player.Faction` comparison with the actor's faction.
- Make `Research`, `Machines`, building ownership and money per faction.
- Decide human-vs-human aggro and self-defence.
- Extend saving: it writes a single CAMERA record (SaveCapture.cs:82-98, SaveLoader.cs:61-65), so it needs a Meitou-specific extension.

### 5. What the snapshot publishes per player

`World.Publish()` (WorldPublish.cs:23-105) runs after `Tick++` (World.cs:232).

Per-viewer inputs:
- `selected`, `inspected` and `detailed` come from `Player.Selection` and `Player.Inspected` (42).
- **Detailed only:** `Skills`, `AllSkills`, `SkillXpMultipliers`, `Hud`, `HealthParts`, `Bounties` (57-67).
- **Selected only:** `Inventory`, `ItemGrid`, `Path`, `CarriesShackles` (58,63,69,82).
- `StealthKoChance` uses `koSlot`, the first selected character that can knock someone out (29-31, 96-97).
- **Whole snapshot:** `Trade`, `Conversation`, `DialogueRequest`, `Towns`, `Camps`, `SquadAi`, `PlayerSquads` (all squads of the faction, 121), `ActiveSquad`, `InspectedBuilding`, the open container as a machine (`OpenContainerView`, 111-118), `Research`, `Crafts`, `UnlockedBuildings` (101-104).
- `CharacterSnapshot.IsPlayer` (52) means "the player faction", which becomes viewer-relative in PvP.

**Can it take a viewer id without touching the hash?** Yes, with two caveats:
- `Publish` only reads hashed state. Its one write is `PublishHeal`, which mutates `cold.HealShown` and `HealTick` and enqueues floaters (Floaters.cs:42-48). Those fields are not hashed, but this must run once per tick, not once per viewer. Check `animations.Publish` and `ragdolls.Publish` for the same issue.
- Split it into a shared part (positions, animation, `Body`, `Orders`, `Jobs`, built once) plus a per-viewer overlay, `Publish(PlayerSession v)`.

The catch is the hash: if `Selection` and `Squad` move to per-viewer sessions, `PlayerState.Hash` (102,104-108) changes. That moves the pinned `"player"` golden hashes (tests/Meitou.Tests/Simulation/GoldenHashTests.cs:55) unless session 0 keeps the old hashing. The repo's usual way is a conditional term, as in Squads.cs:113.

Which architecture this suits:
- **(S) host-authoritative:** the per-viewer snapshot is the natural replication unit.
- **(L) lockstep:** sessions must be world state, identical on every peer, and each peer publishes only for its local viewer.

### 6. Required changes, highest priority first

**P0: needed for any multiplayer**
1. **`SimCommand.Issuer` plus an ingress gate** (Commands.cs:10-14, 41).
   - The client resolves empty lists itself; the 12 fallback sites in section 2 then become single-player only.
   - The gate rejects foreign IDs, `Debug*` (DebugCommands.cs:9) and client `Paid` (CharacterEditing.cs:21).
2. **`Squad.Owner`** (Squads.cs:24 area), with:
   - a conditional hash term (Squads.cs:113 pattern);
   - an owner check in `Own()` (SquadManagement.cs:144);
   - `PlayerSquads(world, owner)` (46-54) and `KeepActive` per owner (92-98);
   - a save key.
3. **Split `PlayerState`:**
   - `FactionState`: `Faction`, `Money`, `SquadAi`, `Towns`, `Camps`, plus `World.Research` and `World.Machines`.
   - `PlayerSession[]`: `Selection`, `Squad`, `Inspected*`, container and door walks, `InCreation`, `CreationRaces`.

   This touches about 157 `world.Player` references. Hash every session field that changes the simulation (door and container walks too).
4. **Per-session conversation and trade:** DialogueSystem.cs:81-85 and 124-136 (`DialogueChoice` and `EndConversation` keyed by issuer), TradeSystem.cs:98-174. Also per-session container and door walks (Containers.cs:24-84, LockSystem.Doors.cs:104-141).
5. **Pause and interface commands:**
   - Drop the modal-window pause in multiplayer (PlayerInterface.Faithful.cs:17).
   - Make `ApplyPausedInterfaceCommands` (World.Interface.cs) apply only under a global pause, with tick-stamped ordering.

**P1: co-op polish**
6. A join path for later humans in `StartPlayer` (PopulationSystem.Player.cs:85-92). Make creation (`InCreation`, `FinishCreation`) per session (PlayerState.cs:193).
7. Address `World.Post` to a session or faction (World.cs:268; about 60 call sites).
8. Recruits join the talking human's active squad (Recruit.cs:28-29).
9. `Prune` fallback per session (PlayerState.cs:84-95); dismissal removes from every session (SquadManagement.cs:244).
10. Campaign announcements should test player characters, not the camera (PopulationCampaigns.cs:976). `FocusCommand` becomes per session, or host-only.
11. `Publish(viewer)` split, with `PublishHeal` run once (WorldPublish.cs:23-105, Floaters.cs:39-48).

**P2: PvP only**
12. A set of player factions in `FactionRelations`, player-to-player relations, and synthetic factions (FactionData.cs:155-237, PopulationData.cs:172-176).
13. Replace the roughly 45 `world.Player.Faction` comparisons with the actor's faction:
    - Construction*, Production*
    - LockSystem.Doors.cs:109,148
    - TradeSystem.cs:130,208
    - ArrestSystem.Bounties.cs:73-201
    - Dialogue*

    Then redefine `IsPlayer` and human-vs-human aggro (AggroSystem.cs:237).
14. Per-faction `Research`, `Machines` and buildings, and a save-format extension beyond the single CAMERA record.