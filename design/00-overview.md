# Multiplayer design: overview

Design only; no engine code is changed by this branch. Code facts were checked against upstream `master` at **`a1ef2e06f`** (19 November
2026; the research in `../research/` was written against `315d26ecf`, 82 commits earlier). Paths: `Sim/` = `src/Meitou.Simulation/`,
`Game/` = `src/Meitou.Game/`, `Data/` = `src/Meitou.Data/`, `docs/` = upstream `docs/`.

Every file marks its statements: **Fact** (read in the code or docs at that commit, with its place), **Proposal** (this design),
**Not checked** (believed, not read or not measurable here). Nothing was built or run: all numbers come from upstream docs or arithmetic.

## The architecture on one page

1. **One world host, three ways to run it.** A `WorldHost` (proposal, in Simulation) owns the world, its clocks, zone and navmesh
   streaming, weather, saving and the server's rules. It needs no window and no GPU. The game process holds one for single-player
   ("internal server"), opens it to friends ("open to friends"), or runs it alone (`MeitouClient --server`, the dedicated server).
   [01](01-server-host.md)
2. **The server owns all simulation state.** A client sends commands. Each command gets a sender on arrival and passes one permission
   gate before any system sees it. Clients never simulate. [03](03-commands-and-authority.md)
3. **Players are first-class.** `World.Player` becomes `World.Players`: per-player state (selection, purse, open windows, held item) and
   per-faction state (name, research, map knowledge, relations). Each player faction is a real faction; relations between player
   factions change. [02](02-players-and-factions.md)
4. **A client is a snapshot player.** The game already draws and fills its windows from `WorldSnapshot` pairs. The server sends each
   client the parts of that snapshot it may see (shared world by zone of interest, plus its personal view), as changes under a byte
   budget; the client rebuilds `WorldSnapshot` objects and the existing renderer and UI run on them. [04](04-replication.md)
5. **Simulate where players are.** Zones load round every player's characters and view anchor instead of one camera; everything else
   stays in today's off-screen tier. All areas live in one process for the first milestone; the boundary for several simulators is
   named and kept clean, not built. [05](05-areas.md)
6. **One protocol, three transports**: in-process, UDP by address, Steam's old peer-to-peer API for joining a friend.
   [06](06-transport-and-join.md)
7. **Rules, mods and the save are the server's.** [07](07-settings-mods-saves.md)

```
 MeitouClient (game)                         MeitouClient --server
 ┌───────────────────────────────┐           ┌──────────────────────┐
 │ renderer, audio, UI, input    │           │  console / admin     │
 │        │ WorldSnapshot ▲ cmds │           │                      │
 │  ClientSession ───────────────┼── net ────┤► PlayerGate          │
 │   (local: by reference)       │           │   │ sender, checks   │
 │  ┌─────────────────────────┐  │           │  WorldHost           │
 │  │ WorldHost (internal)    │  │           │   clocks, zones, nav,│
 │  │  World + systems        │  │           │   weather, saves     │
 │  └─────────────────────────┘  │           │  World + systems     │
 └───────────────────────────────┘           └──────────────────────┘
```

## Terms

| Term | Meaning |
|---|---|
| World host | The object that runs one world: `World`, systems, clocks, streaming, weather, saves, rules. No Game types. |
| Internal server | A world host inside the game process. Single-player is an internal server with one local player. |
| Dedicated server | The same host with no local player and no window. |
| Player | A person's account on one server: an id, a name, a faction, per-player state. Exists while offline. |
| Player faction | A faction owned by one or more players. The first one is the original's player faction record. |
| Client session | What the game talks to: "send this command", "give me the current and previous snapshot". Local or remote. |
| Shared view | The part of a snapshot that is the same for everybody who can see it (characters, buildings, doors, items). |
| Personal view | The part that depends on who looks (selection, squads, trade, dialogue, map, research, inspected details). |
| View anchor | One point per player (their camera focus, leashed to their characters on big servers) that loads a 3 x 3 zone block. |
| Area | A set of zones simulated by one simulator. Milestone 1 has one area: the world. |
| Rules | The server's gameplay settings (43 values today, spread over the client's config). |

## What the code already gives us (facts that shaped the design)

- The simulation is already headless: `Meitou.Simulation` references only Core and Data (`Sim/Meitou.Simulation.csproj:9-10`); tools run
  whole saves with no GPU (`tools/Meitou.Tools/ProbeWorld.cs:261-327`).
- The UI already sends almost everything as commands: 158 `SimCommand` record types in 35 files, 176 `Say(new ...)` sites
  (`Game/PlayerInterface.cs:784-788`).
- The game already reads the world through snapshots: 3,167 of its 5,837 state reads are on snapshot types. Of the 2,670 others, about
  1,128 are value objects that already travel inside snapshots, about 300 are immutable data, 322 are `World.Tick` used to stamp commands,
  130 are event records, and about 480 are true live reads, most of them in developer tools ([04](04-replication.md#live-reads)).
- The world already loads several disjoint places for one player: a box round each player character (`Sim/Population/PopulationSystem.cs:688-697`).
  Only the camera block and the step-rate eye are single (`:72`, `:96`; `Sim/World.cs:617`).
- Ownership of buildings, towns and fixtures is already a faction index, not a "player" flag
  (`Sim/Construction/BuildingRegistry.cs:98,254`; `Sim/Locks/Fixtures.cs:289`).

What it does not give us: any notion of a second player (no id, no sender, one `PlayerState`), a faction list that can grow
(`Data/Gameplay/FactionRelations.cs:43-52`), a stored form for a player's squad, a shared/personal split of the snapshot, any network code.

## Where this departs from the accepted outline

| Outline | This design | Why |
|---|---|---|
| Single-player runs through the same path as multiplayer | Same host, same commands, same gate, same per-player publish; but the local player's snapshots and commands pass **by reference**, not through the wire format. A `--loopback-net` switch and the tests push single-player through the full encoder. | The snapshot ring and tick-ahead exist to keep single-player frames short (docs/simulation.md:37-59). Encoding every tick for nobody would cost that. |
| A dedicated server is "the host alone" | It is `MeitouClient --server` first: the same executable, branching before any window or Vulkan call. A slim `meitou-server` executable follows when the mod contract leaves the Game assembly. | Code mods link `MeitouClient.dll` today (docs/modding.md:26-32). Running the server from that assembly makes existing mods load unchanged on day one. |
| Offline characters are kept "as unloaded squads are today" | A new stored form is needed: today's `StoredSquad` exists only for squads the population made; a player squad is never unloaded (`Sim/Population/PopulationStored.cs:9-13`; `PopulationSystem.cs:694`). The design reuses the `StoredSquad` record and adds the player path. | Fact found in the code. |
| An unattended base produces nothing while no member is near | Production stops when no member of the faction is **online**, not when none is near. | Today a base keeps producing while its owner is elsewhere (`UnloadedProduction`, `Sim/Production/ProductionSystem.cs:55,1318`). Stopping it for an online player would change single-player behaviour. |
| Camera decides the simulated area | Each player has one view anchor. Small servers leave it free, as today; big servers leash it to the player's characters. | A free camera on a public server lets one player make the server simulate, and reveal, any place. |
| "About 40 settings", "about 110 commands", "player faction in 95 files" | 43 gameplay settings (plus 2 for autosave), 158 commands, `Player.Faction` at 309 places in 96 source files, `.IsPlayer` at 301 in 109. | Recounted at `a1ef2e06f`. |
| Players become factions in one refactor | Two stages. First "several players" (each squad has an owner; per-player state), which is enough for friends in one faction. Then "several player factions". | The first stage touches about 110 places; the second about 310 more and the relations table. Friends can play after the first. |

## The first milestone

**In:** the host without a window; commands with a sender and a gate; time as commands, with speed voting; several players; per-player
trade, dialogue and inventory; trade between players (items, cats, characters); shared and personal views; replication with interest by zone and a byte budget; joining by address, by
Steam friend (Windows), and hosting from the game's menu; the server's rules, mod list check and save; offline players; then separate
player factions with relations, joining a faction, raids and crimes per faction. A whole playthrough for 2 to 8 friends.

**Checkpoint inside it:** after package 24 of [08](08-milestones.md) friends can play together **in one faction**. Separate factions are
packages 25 to 29. Decided on 19 November 2026: milestone 1 is complete only with them; the checkpoint is for testing with friends.

**Not in:** more than one simulator process; players' machines simulating; encryption of traffic (password proof only); downloading
non-Workshop mods from the server; a server browser; client-side prediction of movement; Linux and macOS joining through Steam (Kenshi
ships only a Windows Steam library); loading a multi-faction server save in the original game.

## Capacity in one table

Rests on one **Observed** figure: a tick costs about 1.5 ms + 4.5 µs per loaded character at rest (docs/bench.md:179; the row names no
CPU or thread count), a 3 x 3 block loads 150-500 characters (same row), and on unmeasured assumptions listed in
[05](05-areas.md#cost) and [04](04-replication.md#bandwidth).

| Players | Loaded characters (assumed) | Tick at rest | Server upload | Verdict |
|---|---|---|---|---|
| 2 | 300-1,000 | 3-6 ms | 0.03-0.26 Mbit/s | Fine on the host's own machine |
| 8 | 1,000-4,000 | 6-20 ms | 0.8-2 Mbit/s | Fine; spikes while blocks load |
| 32 | 3,200-9,600 | 16-45 ms | 3-8 Mbit/s | Needs the leash, the far cadence and load shedding; a real machine |
| 200 | 8,000-60,000 | 38-270 ms | 20-50 Mbit/s | Not in one process unless players cluster; needs areas |

## Reading order

[01 host](01-server-host.md) · [02 players](02-players-and-factions.md) · [03 commands](03-commands-and-authority.md) ·
[04 replication](04-replication.md) · [05 areas](05-areas.md) · [06 transport](06-transport-and-join.md) ·
[07 settings, mods, saves](07-settings-mods-saves.md) · [08 milestones](08-milestones.md) · [09 open questions](09-open-questions.md)

## Corrections from the code check (19 November 2026)

Every statement of fact in this file was checked against the code at `a1ef2e06f`. The rows below did not hold as written: where a row
and the text above differ, the row is right. Line numbers are this file's. WRONG: the code does something else. OFF: the right
thing at another place or with another number, or cited lines that do not say it. UNSUPPORTED: no code found that backs it.

| Line | Statement | Verdict | What the code says | Corrected |
|---|---|---|---|---|
| 84 | "a player squad is never unloaded (`PopulationStored.cs:9-13`; `PopulationSystem.cs:694`)" | OFF | The cited lines do not say it. PopulationStored.cs:9-13 describes town squads becoming `StoredSquad`; PopulationSystem.cs:694 only filters living `IsPlayer` characters of non-dormant squads for the zone box. No code that unloads an `IsPlayer` character was found, so the claim is an inference. The code does know unloaded platoons of the player's faction (PopulationSystem.cs:588-594 "unloaded ... or loaded"; PopulationRoaming.cs:268 "a platoon of the player's faction ... stands still"), so there are unloaded player-faction platoons today. | "No code unloads a player's characters (the zone box at `PopulationSystem.cs:688-697` only asks for zones round them); `StoredSquad` is made only for town squads (`PopulationStored.cs:9-13`). Platoons of the player's faction can exist unloaded (`PopulationSystem.cs:588-594`) and stand still (`PopulationRoaming.cs:268`)." |
| 68-69 (also 00:65-69 list) | "3,167 of its 5,837 state reads are on snapshot types ... 2,670 others ... 1,128 ... 300 ... 322 ... 130 ... about 480" | UNSUPPORTED | No place or counting method is given and the classification (value object, immutable data, event record, live read) is not recountable with grep. The only recountable piece nearby: `Commands.Enqueue` at 93 lines. | Mark as **Not checked**, or give the grep and classification used. |
| 59 and 87 | "Rules (43 values today, spread over the client's config)", "43 gameplay settings (plus 2 for autosave)" | UNSUPPORTED | The only gameplay record found is `GameplaySettings` (Game/Settings/GameplaySettings.cs:12-22) with 10 fields (8 plus AutoSave, AutoSaveMinutes). No code or doc defines a set of 43. The design's own list (07) is not in this packet. | State which fields make the 43 (file and list), or mark **Not checked**. |
| 85 | "Today a base keeps producing while its owner is elsewhere (`UnloadedProduction`, `ProductionSystem.cs:55,1318`)" | OFF | Right places (`ProductionSettings.UnloadedProduction` :55, `Live()` :1318), but it is a Meitou default-on user setting (`UserConfig.UnloadedProduction`, UserConfig.cs:390; `ProductionSystem.cs:51-54` "false: they stop there ... as the game does"). The original stops; it is not unconditional. | "With the default `UnloadedProduction = true` a base keeps producing in unloaded zones (`ProductionSystem.cs:55,1318`); the original game stops it and farms catch up on load." |
| 87 | "158 commands" | OFF (tiny) | 163 `: SimCommand/OrderCommand/DebugCommand/SquadCommand/HeldCommand` matches in 35 files, minus 4 abstract bases = 159; `DebugCommands.cs` alone has 35 concrete records (see 03). Within tolerance. | "about 159 concrete command records in 35 files". |
