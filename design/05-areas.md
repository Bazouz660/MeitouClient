# 05. Areas: what is simulated, what it costs, and the door to several simulators

Upstream `a1ef2e06f`. `Sim/` = `src/Meitou.Simulation/`, `Game/` = `src/Meitou.Game/`, `PS` = `Sim/Population/PopulationSystem.cs`,
`ZA` = `Sim/Population/ZoneActivation.cs`.

## Today (facts)

The world is 64 x 64 zones of 4608 units (`src/Meitou.Data/World/WorldLayout.cs:13-18`). Every 15 ticks (`CheckEveryTicks`,
`Sim/Population/PopulationData.cs:951`) `RefreshZones` (PS:683) asks for:

| Asked by | Zones | Linger | Single or many |
|---|---|---|---|
| The camera focus (`FocusCommand`, a position; `Sim/Commands.cs:130`) | 3 x 3 block round a centre zone that moves only when the focus is 4147.2 off its middle (ZA:28; PS:577-616) | 4 s (ZA:39) | **one** (`_focus` PS:72, `_loadCentre` PS:96) |
| Each living `IsPlayer` character not in a dormant squad | the zones a +-340 box touches, plus a ring of one zone with Fast zone hopping (ZA:22,105-113; PS:584-697) | 6 s (ZA:45) | many already |
| Each platoon of the player faction | the same box at its place (PS:588-594) | 6 s | many already |
| A loaded town | its coverage list, extending nothing (PS:641-652) | - | per town |

With no focus the block centres on the first player character; with neither, nothing loads (PS:699-700). Lingers run in real time
(`LingerSpeed` = the game speed, PS:668). A zone counts as loaded only when its building collision is read and the host's `ZoneGate`
says its navmesh is live (PS:630-635). Towns unload with their zones (grace 0), wild sites after 60 s, roamers after 10 s
(`PopulationData.cs:940-985`). An unloaded town's squads become pinned platoons holding a `StoredSquad`: each member's whole cold state
and place (`Sim/Factions/Platoons.cs:251,313`; docs/simulation.md:367-369).

**Step rate.** A character steps every tick when it is within `NearRadius` (8000) of the one eye `ViewPosition`, is a led player
character, or is within 1500 of one; otherwise once in `FarTickPeriod` ticks (the game 4, default 1) (`Sim/World.cs:617-634,1131-1208`;
docs/simulation.md:70-90). With no `ViewPosition` everyone steps every tick (:1145-1151). The one camera also feeds `World.View`
(a walker out of view steps on twice the tick time; the medical time budget; `Sim/Bodies/MedicalTimeBudget.cs:109`).

**Off-screen tier.** Unloaded platoons that are not a town's own step at every population look, in parallel, moving in straight lines
or along roads at 25-90 units a second; with the Meitou switches they fight (every 2 s, within 300), heal on return, and towns keep
their schedule; pools and campaigns run on the game clock everywhere (docs/simulation.md:405-569). Player squads are excluded: they are
never unloaded (`Sim/Population/PopulationRoaming.cs:268`; `Sim/Population/PopulationOffscreen.cs:49`).

**Navmesh.** Zones are built by an own builder with a disk cache (`Sim/Navigation/NavSystem.cs:15`); the loaded mesh is one immutable
set of zone meshes with border links (`Sim/Navigation/NavWorld.cs:22-44`). The streaming loop is in the game
(`Game/GameHost.cs:1334-1483`): it wants the population's requested zones plus zones long routes pass through, keeps a left zone 120 s,
and holds at most **60 zones** beyond the stale ones (:1347-1351, :1441-1454). Every publish or unload re-resolves the kept sets of all
loaded zones (`Sim/Navigation/NavMeshService.cs:235-257`, "tens of ms" by its comment). The build queue is ordered for one player's
characters (`NavSystem.Favour`, :109).

**Structure.** Worker partitions are slot ranges, not places (`World.cs:1213-1219`). There are no per-zone character lists; the
`SpatialGrid` (25-unit cells) is rebuilt each tick for neighbour queries (`Sim/SpatialGrid.cs:5-11`). `CharacterId` is slot plus
generation, local to one `CharacterTable`.

### Cost figures (all **Observed** upstream; none measured for this design) {#cost}

| What | Figure | Source |
|---|---|---|
| Tick at rest | about 1.5 ms + 4.5 µs per loaded character (4.6 ms at 715, 9.2 ms at 1660) | docs/bench.md:179. In the game, far cadence 4; the row names no CPU and no thread count |
| Tick while a block's newcomers spawn | 12-13 ms | same |
| Characters in one 3 x 3 block | 150-500 | same |
| Far cadence gain | about half the characters skip a tick; the tick fell 13.7 to 12.3 ms, then 12.8 to 11.4 | same |
| Slow-world phase | 1.5-4.9 ms a tick, ragdolls 0.6-2.9 of it | same; docs/simulation.md:89-90 |
| Town spawn | first spawn of an 8-squad town 65 ms (JIT included); later 0.06-12 ms; unload 0.02 ms | docs/simulation.md:65 |
| Ragdoll tick when a zone's downed bodies re-fall | 45-60 ms before budgets, 1-4 ms after | docs/bench.md:179 |
| GC pauses during spawns | 4-12 ms, sometimes 10-25 | same |
| Off-screen tier | about 0.03 ms a tick with 2000 extra stand-ins | docs/simulation.md:482-484 |
| Publish | 0.6-1.0 ms a tick at about 1,150 loaded | docs/simulation.md:88-89 |
| Navmesh zone, not cached | 3-13 s on 12 cores at the in-play share; a cached one is live in milliseconds | docs/game/pathfinding.md:688 |
| Navmesh tile builds | allocated about 13 GB in 5 s and starved the simulation on every core | docs/simulation.md:640-642 |
| Thread scaling of the tick | **no figure exists** | - |
| Memory per character, per zone, per navmesh zone | **no figure exists** | - |

## Target (proposal)

### What is loaded

One rule, applied per player and unioned:
- **Characters load their surroundings**: unchanged, the box round each of a player's characters. A squad the player put to sleep and
  an offline player's characters ask for nothing.
- **Each player has one view anchor** that loads a 3 x 3 block, as the camera does today. The client reports it
  ([03](03-commands-and-authority.md#the-gate), `ViewReport`). `_focus` and `_loadCentre` become one entry per player.
- **The leash** (rule `view.leash`, units; 0 = none): the anchor is clamped to within that distance of the player's nearest character.
  Discovery already clamps the focus to 2250 of a player character (`CameraReach`, PS:452). With a leash a player's camera can still fly
  anywhere: beyond the leash they see terrain and buildings from their own data and no characters.
- **A cap per player** (rule `view.maxGroups`): how many separate places one player keeps loaded. Beyond it, the player's farthest squad
  that nobody else's loading covers is *stored* like an offline squad ([07](07-settings-mods-saves.md#offline-players-and-bases)) and
  shown on the map as waiting; it comes back when the player's camera returns or a slot frees. Off by default below 16 players.

| Preset | Leash | Groups per player | Far period | For |
|---|---|---|---|---|
| Single-player, co-op (up to 8) | none | unlimited | 4 | today's behaviour for each player |
| Community (up to 32) | 6000 | 3 | 4 | |
| Large | 3000 | 2 | 6 | with areas, later |

Lingers stay in real time times speed. Zone timers of a paused server do not run, as today (docs/simulation.md:25).

### Step rate {#step-rate}

`ViewPosition` becomes a list of eyes (one per online player) and `World.View` a list of frusta. A character is near when it is within
`NearRadius` of **any** eye, or as today for player characters. With one player the list has one entry and nothing changes. A dedicated
server sets the far period from its preset; it does not default to 1 as headless worlds do now.

### What happens where nobody is

Nothing new: the off-screen tier, pools and campaigns already run on the game clock for the whole world. Two things to add:
- Player squads stored by the group cap or by going offline do **not** travel, fight or heal off-screen. They are frozen where they were.
- A trade caravan or patrol that a player follows on the map is an ordinary stand-in; it becomes real when a player's zones reach it.

### Cost and limits

Assumptions, stated so they can be replaced by measurements: the bench line holds on the server's CPU; players overlap as the middle
column says; "comfortable" is a rest tick of 20 ms, leaving 13 ms for spawn spikes and GC pauses; the hard limit is 33 ms, about 7,000
characters.

| Players | Distinct loaded characters per player (assumed) | Loaded | Rest tick | Verdict |
|---|---|---|---|---|
| 2 | 150-500 | 300-1,000 | 3-6 ms | comfortable on a player's own machine beside the renderer |
| 8 | 125-500 | 1,000-4,000 | 6-20 ms | comfortable; each player entering a new block costs a 12 ms spawn tick |
| 32 | 100-300 | 3,200-9,600 | 16-45 ms | needs the community preset; at the high end the server runs slow |
| 200 | 40-300 | 8,000-60,000 | 38-270 ms | only if players gather (events, a few towns); otherwise several simulators |

Other limits that bite before the tick does:
- **Navmesh.** Eight players apart want 72 or more zones: over the 60-zone cache, which would then drop zones in use. The limit becomes
  a rule sized from the player count, and the "re-resolve everything on every change" publish (tens of ms) must become per changed zone
  before 32 players. A zone nobody has visited takes 3-13 s to build; on a server that is a player standing in a zone that is not live
  yet. A dedicated server therefore pre-builds on start the zones that hold towns and bakes the rest in idle time
  (`--bake-navmesh` to do it all offline; duration **not known**, at most 4096 zones x 13 s). The cache key hashes file write times and
  absolute paths (`Sim/Navigation/NavContentHash.cs:25-160`), which is fine for a server's own disk and useless for sharing a cache
  between machines.
- **Spawn spikes.** Several players crossing into new blocks in the same second stack their 12 ms spawn ticks. The host spreads town
  builds over ticks (one finished town a tick; today each tick already finishes at most one roaming or wild build,
  docs/simulation.md:379).
- **Memory.** Unknown. To be measured in package 16 with 8 simulated players apart.

### Under load

In this order, each a rule with a threshold on the smoothed tick time:

| Step | Effect on players |
|---|---|
| 1. Raise the far period (4 to 6 to 8) | Distant NPCs react later; nothing near a player changes |
| 2. Shorten lingers, shrink view blocks from 3 x 3 to the character boxes plus one ring | Characters far from your own pop in later |
| 3. Enforce the group cap, storing far squads | Your third outpost goes to sleep |
| 4. Let the clock slip: ticks over budget are dropped, as the single-player clock already does (`AchievedSpeed`, docs/simulation.md:13) | The whole world runs slower for everyone, evenly; clients show "server at 80 %" |
| 5. Refuse new connections | |

The server never drops a player's own characters and never simulates a place at a different speed from another: one clock.

## The boundary for several simulators (designed, not built)

Decision 9 of the brief: the server alone simulates, in one process for now; several server processes later; players' machines at most
an optional setting after that. What must be true *now* so that later is possible:

| Concern | The boundary | What milestone 1 does about it |
|---|---|---|
| Who owns a zone | An **area** is a set of zones with one simulator. `IAreaMap.OwnerOf(zone)` | One area. The host asks the map anyway |
| Things that are global, not spatial | A **world coordinator** owns: the clock and speed, players and factions, relations and diplomacy, research, the unique-character table, world states, campaigns and war managers, town pools, the off-screen tier, the save | These already live in `World`-level objects and the population's serial phases. New code reaches them through `WorldHost` services, not through a `World` it assumes is the only one |
| A squad crossing a border | It is stored and re-made: `StoredSquad` already carries every member's cold state and place and is what a town unload produces today. A hand-over is "store here, activate there" | Offline squads and the group cap exercise the same store and activate path for *player* squads, which today has no such path |
| Seeing across a border | Each area keeps read-only ghosts of characters within sight range of its edge, fed by the neighbour's shared view | The shared view is already a self-contained description of a character; replication does not assume one source |
| A fight across a border | The hard part. Options: move the border (areas are sets of zones, re-cut at a tick boundary), or hand both parties to one side. Not solved here | Nothing; noted as the main risk of areas |
| Ids | `CharacterId` is local to a table. Across areas an id needs the area in it | The wire format gives ids 32 bits and treats them as opaque; `LastingIds` (`Sim/LastingIds.cs`) already give characters a save-stable id |
| Clients | A **gateway** holds the connection and merges the streams of the areas a player's interest touches | `PlayerGate` is written as that gateway with one source |
| Paths and navmesh | Each area loads its zones' meshes plus a border ring; long routes use the coarse route and roads, which are global data | Zone streaming per wanted set, no fixed limit |
| Determinism | Not required between areas: clients do not simulate, areas exchange state, not inputs | - |

What would break this, and so is ruled out in milestone 1 code: a new static that holds world state (32 changed statics exist already,
`../research/state-inventory.md`); replication that reaches into `World` instead of the published views; commands applied anywhere but
through the gate.

Players' machines as simulators (the model of the existing Kenshi multiplayer mod, `../research/anarchy-notes.md`) would be an area whose
simulator is a client. It needs everything above plus trust: a result from a player's machine must be checked or accepted on faith. It
stays outside this design, as decided.

## What must not change in single-player

- With one player: one anchor (the camera focus), one eye, the same zone set at the same ticks. The golden scenarios set no
  `ViewPosition` and no far period (`tests/Meitou.Tests/Simulation/GoldenHashTests.cs`), and `ActiveZones`, `LoadedZones` and
  `ViewPosition` are not hashed (`World.cs:540-548,615`).
- The 60-zone navmesh cache and the 120 s keep remain the defaults of a local host.
- Parity: the camera's 3 x 3 block, the timers 4 and 6 s and the per-character box are the original's; the leash and the cap are
  options that are off unless a server turns them on.

## Not checked

- Navmesh memory per zone and whether `NavWorld` needs loaded zones to be adjacent (no such requirement was seen).
- `NavBuildQueue` internals; how `Favour` would order builds for many players.
- `RagdollGeometry` keeps the loaded zones plus the 24 most recently asked (docs/simulation.md:909): whether 24 is enough with many anchors.
- The sector "under the camera focus" is never refilled with homeless squads (docs/simulation.md:518): with many anchors this becomes
  "under any anchor"; the effect on wilderness population was not thought through.

## Corrections from the code check (19 November 2026)

Every statement of fact in this file was checked against the code at `a1ef2e06f`. The rows below did not hold as written: where a row
and the text above differ, the row is right. Line numbers are this file's. WRONG: the code does something else. OFF: the right
thing at another place or with another number, or cited lines that do not say it. UNSUPPORTED: no code found that backs it.

| Line | Statement | Verdict | What the code says | Corrected |
|---|---|---|---|---|
| 31-32 | "Player squads are excluded: they are never unloaded (`PopulationRoaming.cs:268`; `PopulationOffscreen.cs:49`)" | OFF | PopulationRoaming.cs:268 says a platoon of the player's faction *stands still* (no travel), and PopulationOffscreen.cs:49 skips platoons of the player faction in the off-screen *fights*. Neither says unloaded never happens. They show the opposite of the first reading: unloaded platoons of the player faction exist. Characters of player squads are never put out of the world by any code found. | "Platoons of the player's faction do not travel (`PopulationRoaming.cs:268`) and do not fight off-screen (`PopulationOffscreen.cs:49`); no code unloads a player's characters." |
| 118-120 | "The cache key hashes file write times and absolute paths (`NavContentHash.cs:25-160`)" | OFF | `Stamp` mixes the file *name* (not path), size and write time (NavContentHash.cs:144-149). A resolved absolute path is mixed only for collision files (`Add(file)` :111). The layer name is mixed, not its directory. | "The key hashes each input file's name, size and write time (`NavContentHash.cs:144-149`) and the resolved path of collision files (:111); copying the files to another machine changes the write times, so the cache is not shared." |
| 37-38 and 116 | "Every publish or unload re-resolves the kept sets of all loaded zones (`NavMeshService.cs:235-257`, 'tens of ms' by its comment)" and "the 're-resolve everything on every change' publish (tens of ms) must become per changed zone" | OFF | `Publish` (:235-247) resolves all zones' kept sets (`ZoneKeptClosure.Resolve`, :237) but republishes only zones whose set changed (`continue` at :239-240, `world.With` :243). The "tens of milliseconds" comment (:256) is about rebuilding the combined mesh on an unload publish. | "A publish resolves the kept sets of all loaded zones (`NavMeshService.cs:237`) and relinks only changed zones (:239-243); an unload rebuilds the combined mesh in 'tens of milliseconds' (:256)." |
| 152 | "`LastingIds` (`Sim/LastingIds.cs`) already give characters a save-stable id" | OFF | Ids are given only to characters a code mod asks for (`GetOrGive`) and nothing is held, hashed or written until a mod asks or a save with ids is loaded (LastingIds.cs:7-12). Ordinary characters have none. | "`LastingIds` can give a character a save-stable id on request (`GetOrGive`); today only code mods ask." |
| 157-158 | "a new static that holds world state (32 changed statics exist already, `../research/state-inventory.md`)" | UNSUPPORTED | The research file is outside this packet and the count was not checked here. | Keep as **Not checked** unless recounted. |
