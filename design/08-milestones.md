# 08. Milestones: the work packages

The ordered path to "a whole playthrough with a few friends". Upstream `a1ef2e06f`. `Sim/` = `src/Meitou.Simulation/`,
`Game/` = `src/Meitou.Game/`, `tests/` = `tests/Meitou.Tests/`.

How to read it:
- Each package is one pull request, or a short series where it says so. Size: **S** a few days, **M** one to two weeks, **L** more;
  these are guesses by reading, nothing was built.
- **Keeps**: what must be unchanged in single-player after the package. For every package this includes: all pins of
  `tests/Simulation/GoldenHashTests.cs` unchanged, the thread-agreement test at 1, 4 and 16 threads passing, the full test run green.
  It is not repeated below.
- **Tests** are proposals. In this project the owner approves every test before it is written (CLAUDE.md, "Build"); each line says what
  the test would catch that today's tests do not.
- A package that changes `ModContext`, `ModHost` or the manifest updates `docs/modding.md` and `docs/modding-dependencies.md` in the same
  change; every package writes the contract it creates into upstream `docs/` (engine.md, simulation.md, launch-options.md, a new
  `docs/multiplayer.md`), as facts, not as plans.

Three stages. **A** changes no behaviour and ships in single-player one package at a time. **B** adds the network and ends with friends
playing in one faction. **C** makes player factions separate.

Decided on 19 November 2026: milestone 1 is all three stages. The end of stage B (after package 24) is a checkpoint to test with
friends, not a release. The packages are built in the main repository as ordinary pull requests.

## Stage A: single-player on the new seams (no behaviour change)

### 1. The host owns the clock and the tick (M)
- **Scope:** `WorldHost`, `HostClock`, `TickAhead` in a new `Sim/Hosting/`. Move from `WorldSession`: world ownership and `ReplaceWorld`
  (`Game/Host/WorldSession.cs:147-195`), `Advance`/`AdvanceSimulation`/`SimulationTick` (:240-326), the loading hold (:336-357), tick
  ahead (:366-568). `WorldSession` keeps camera, input, alpha and the shown pair and calls the host.
- **Files:** `WorldSession.cs`, new `Sim/Hosting/*`, `Game/GameHost.cs:636-660`, `Game/Host/GameLoop.cs`, `Game/Host/MetalGameLoop.cs:786-804`.
- **Keeps:** tick order, the tick-ahead thresholds (4 ms on, 2 ms off), frame times in the play benchmark within noise.
- **Risks:** the Metal loop cannot be built on the Windows CI path; a subtle change in when `GameHours` is copied (`WorldSession.cs:316`).
- **Tests:** a host driven with a fake real-time source runs the same ticks and game hours as a recorded `WorldSession` run (catches a
  changed tick budget or clock copy; nothing tests `WorldSession`'s loop today).

### 2. Zone and navmesh streaming in the host (M)
- **Scope:** `ZoneStreamer` takes `GameHost`'s nav region (`Game/GameHost.cs:1334-1537`) and the six `ZoneGate` assignments
  (`GameHost.cs:687`, `Game/Host/GameSaves.cs:500`, `MetalGameLoop.cs:851`, `Game/Title/NewGameLaunch.cs:46,123`,
  `tools/Meitou.Tools/ProbeZoneStreamer.cs:93`). The tool uses the host's class and its copy is deleted. The 60-zone limit and 120 s keep
  become parameters with today's values.
- **Files:** those, plus `Sim/Navigation/NavSystem.cs` call sites.
- **Keeps:** the wanted set, the drop order, one repath a second.
- **Risks:** timing of gate-code relabelling against `GateRegionsCommand` (`GameHost.cs:1509-1537`).
- **Tests:** with a scripted focus path over synthetic zones, the streamer asks for and drops the same zones at the same times as the
  constants say (no test covers the streaming loop today; research section 11).

### 3. Rules as one record (M)
- **Scope:** `WorldRules` in Simulation and `RulesBinder` in the host replace `GameSaves.BuildSystems` and `ApplyGameplay`
  (`GameSaves.cs:651-723`), the live callbacks of both loops (`GameLoop.cs:503-549`, `MetalGameLoop.cs:1320-1351`), the world flags
  (`GameHost.cs:650-656`), the per-frame ground item limit (`Game/Host/WorldSync/GroundItemSync.cs:43`) and the tool's hand copy
  (`tools/Meitou.Tools/ProbeWorld.cs:261`). `UserConfig` keeps storing the values; a mapper builds `WorldRules` from it.
- **Keeps:** every setting's effect and default; settings pages untouched.
- **Risks:** `PopulationData.BodyOptions` is shared and mutable; `FixGround` is a static set at startup (`Game/Host/Program.cs:83`).
- **Tests:** for each rule, a world built through the binder has the same system options as one built by today's code path (catches a
  dropped or crossed setting; there are 43).

### 4. Weather on the host's clock (M)
- **Scope:** `HostWeather` steps the regions and implements `IWeatherSource`; `WorldWeather` (`Game/Rendering/Sky/WorldWeather.cs`) keeps
  the camera's presentation and reads region state from the host. The `WeatherWorld` is created by the host, not by the GPU builder
  (`Game/Rendering/Frame/WorldGpuBuilder.cs:238`). Effect zones stay fed by the renderer when there is one.
- **Keeps:** the schedule for a given save and time; save and load of weather (`GameSaves.cs:286-296,513-517`).
- **Risks:** `WeatherWorld` mixes region state with camera state (`src/Meitou.Data/World/WeatherWorld.cs:46-70`); splitting it is the work.
- **Tests:** stepping the regions from the game clock alone gives the same season, weather and strength per region as the camera-driven
  update over a simulated day (catches a schedule that depended on frame timing).

### 5. Saving in the host (M)
- **Scope:** `SaveService`: requests, autosave timer, `PrepareBase`, capture at a boundary, write task, load and swap. `GameSaves` keeps
  the camera view, sky fields, decals, notices, loading stages and `UserConfig` bookkeeping, passed in as `SaveCaptureOptions`.
- **Files:** `GameSaves.cs` (63 writes to simulation state today), `Sim/Hosting/SaveService.cs`.
- **Keeps:** saved files byte-for-byte for the same world (compare folders), autosave rotation, `SettleAhead` before capture.
- **Tests:** a headless host saves and reloads a synthetic world and the carried fields agree, the same assertions as `QuickSaveTests`
  but through the host (catches host-side restore steps lost in the move).

### 6. A windowless run (S)
- **Scope:** `MeitouClient --server [--load <save>] [--config <file>]`: boots data and mods, builds a `WorldHost`, ticks in real time,
  reads console commands from standard input and `--control-dir`, autosaves, exits cleanly. No network. With nobody in the world nothing
  is loaded; a console `focus x z` loads a place for testing.
- **Files:** `Game/Host/Program.cs`, `Game/Host/GameOptions.cs`, `docs/launch-options.md`.
- **Keeps:** every existing launch path.
- **Risks:** hidden GPU or window dependencies in boot (`BootSimulation`, `GameHost.cs:598`); mod loading without `PlayerInterface`.
- **Tests:** `[Slow]`: start with `--server --load` on a real save, run 600 ticks, save, exit code 0 (the first proof that a world runs
  with no GPU inside the shipping executable).
- **This is the first thing to show:** a server that runs the world, with nobody in it.

### 7. Commands have a sender and pass a gate (M)
- **Scope:** `PlayerId`; `SimCommand.Sender` with a default; `Sim/CommandRules.cs` classifying all 158 command types
  ([03](03-commands-and-authority.md#the-gate)); `CommandGate.Admit` called from `World.TakeDue` (`Sim/World.cs:1035-1051`). With one
  player every check passes. Deferred commands are `PlayerId.World`.
- **Keeps:** which commands apply and in what order. `Sender` is not hashed.
- **Risks:** tests and scenarios that order NPCs through no-check commands (`MoveOrder`, `Sim/Movement/MovementSystem.cs:398-436`): they
  must send as `World`.
- **Tests:** every concrete `SimCommand` type has a rule (reflection; catches a new command added without one); a command naming a
  character of another owner is refused, and admitted for the owner (needs package 9's owner; until then against `IsPlayer`).

### 8. Time as commands (S)
- **Scope:** `SpeedRequest`; `HostClock` decides by `time.mode`, only `local` implemented. Route the keys
  (`WorldSession.cs:581-586`), HUD (`Game/PlayerInterface.Ui.cs:1417`), build mode (`Game/Building/BuildMode.cs:200,211`), photo mode
  (`Game/Host/Photo/PhotoMode.cs:550-618`), editor (`Game/Editor/CharacterEditorPanel.cs:535`), pause on load (`GameSaves.cs:573`), menu
  pause (`PlayerInterface.Ui.cs:1107`).
- **Keeps:** every speed and pause behaviour, including the paused interface path (`Sim/World.Interface.cs:27-84`).
- **Risks:** photo mode's free slow motion is a clock feature with no command equivalent; it stays a local-host call.
- **Tests:** none proposed beyond the host test of package 1 extended with speed requests.

### 9. Players and squad owners (M)
- **Scope:** `Player`, `World.Players` with one entry; `World.Player` returns player 0's state. `Squad.Owner`, set wherever a player squad
  is made: spawn (`Sim/Population/PopulationSystem.cs:1231,1308`), load (`Sim/Saving/SavedPopulation.cs:473,517`), recruit
  (`Sim/Dialogue/DialogueSystem.Recruit.cs:54-150`), new squad (`Sim/Factions/SquadManagement.cs`). Saved as `meitou owner`, written only
  when not player 0.
- **Keeps:** hashes (the owner is hashed with a tag only when not player 0); save files of a single-player game.
- **Tests:** a second `Player` added to a synthetic world gets squads that the first player's `Mine` does not list (after package 11).

### 10. Sessions per player (M)
- **Scope:** `TradeSystem` (`Sim/Trading/TradeSystem.cs:291-307`), `DialogueSystem` (`Sim/Dialogue/DialogueSystem.cs:184-200`),
  `CharacterTrading`, container, loot and door state, held item: single fields become a map by player.
- **Keeps:** one player's trade, buy-back receipts (hashed), conversation flow.
- **Risks:** the trade and dialogue code is large and stateful; `World.CloseInventories` closes everything today.
- **Tests:** two players trade with one shop in the same ticks: stock and both purses stay consistent, the second buyer of a single item
  is refused (no test can express two customers today).

### 11. Per-player state uses the acting player (M, 2-3 pull requests)
- **Scope:** in Simulation, `Player.Selection` (37 places), `Player.Money` (43), `Player.Squad` (16) take `Players.Of(sender)` or
  `OwnerOf(character)`. `SquadManagement.Mine` (`:394`) filters by owner. The gate's ownership checks become real. Squad limits per player.
- **Keeps:** everything with one player.
- **Tests:** an order from player B for player A's character is refused; bail is paid from the purse of the jailed character's owner.

### 12. Messages and events have an audience (M, 2 pull requests)
- **Scope:** `World.Post(audience, text)`; visit the 169 `Post` sites in 42 files and give each its audience; `HostEvents` copies
  sounds, floaters, blood, sparks, said lines and logs out of the world at the tick's end (`Game/GameHost.FrameShared.cs:155-186`).
- **Keeps:** every message a lone player sees today, in the same order.
- **Tests:** a message about player A's character reaches A and not B.

### 13. Shared and personal views (L)
- **Scope:** `World.Publish` (`Sim/WorldPublish.cs:146-304`) builds the shared part once and a personal part per player; the 15 viewer
  fields of `CharacterSnapshot` come from a per-player overlay, written in place for the local player. Missing revision counters added
  to shared lists.
- **Keeps:** the snapshot a lone player gets, field for field; publish time at about 1,150 characters (0.6-1.0 ms, docs/simulation.md:88).
- **Risks:** `Publish` runs in parallel chunks and reuses objects in a ring (`WorldPublish.cs:56-106,160-203`); the overlay must not
  break "every property, every publish".
- **Tests:** for a scripted scenario, every property of the snapshot before and after the split is equal (a one-time comparison test
  guarding the refactor); two players' personal views of the same tick differ in exactly the personal properties.

### 14. The client seam (L, 3-4 pull requests)
- **Scope:** `IClientSession`, `LocalSession`. The game stops touching `Session.World`: the roughly 60 shipping-code places of
  [04](04-replication.md#live-reads) (verb facts, `Player.Faction`, `BuildingSync`, `OrdersPanel`, tutorial, pointer), the 268 tick
  stamps, and `PlayerInterface.Say`. Developer tools get the world through `LocalSession.Host`.
- **Files:** `Game/PlayerInterface*.cs`, `Game/Ui/*` (ContextMenuWindow, FurnitureUse, TownPanel, OrdersPanel, PointerView),
  `Game/Building/*`, `Game/Host/WorldSync/*`, `Game/Effects/*`, `Game/DevTools/*`.
- **Keeps:** the UI, exactly.
- **Risks:** the widest package by files. Reads the inventory tool missed (method calls are not in its count; 781 binding errors hid
  some reads).
- **Tests:** a build-time check that no file outside `DevTools`, `Scenarios` and the host glue references `World` (an analyzer or a
  reflection test over the assembly; catches a new live read the day it is written).

### 15. Many anchors, many eyes (M)
- **Scope:** `PopulationSystem._focus`/`_loadCentre` (`Sim/Population/PopulationSystem.cs:72,96`) become per player; discovery's focus
  viewer (:459-468) too; `World.ViewPosition` (`Sim/World.cs:617`) and `World.View` (:481) become lists; `ShownInteriors` per viewer.
- **Keeps:** the zone set and step rates with one anchor.
- **Tests:** two anchors in different zones load both blocks and a character near either steps every tick.

## Stage B: the network, friends in one faction

### 16. Protocol, codecs, loopback (L, 3 pull requests)
- **Scope:** new `src/Meitou.Net`: wire format, the session layer, codecs for every client command and for the views, the in-process
  transport, `RemoteSession` playback ([04](04-replication.md#what-the-client-does-with-it)), a first interest filter (the player's
  zones), `--loopback-net` to run single-player through all of it. A measuring harness: N scripted players spread over a real save,
  reporting tick time, bytes per client, encode time, memory.
- **Keeps:** single-player without the switch loads no network code.
- **Risks:** playback quality (jitter, animation time, ragdolls) can only be judged by eye; the measurements may contradict
  [05](05-areas.md#cost) and [04](04-replication.md#bandwidth).
- **Tests:** every command type round-trips (reflection over the rule table, so a new command without a codec fails); a snapshot
  encoded and decoded equals the original for a client's interest, shared and personal; decoders reject truncated and oversized input;
  `[Slow]` loopback run of a golden-style scenario where the remote session's final snapshot matches the local one.

### 17. UDP, handshake, data identity (M)
- **Scope:** `Udp` transport (LiteNetLib, if approved), the handshake, protocol and game version checks, `DataSetIdentity` in Data,
  password, `--join`, `--server` listening. A second player joins and is given a squad by console command.
- **Tests:** identity is equal for equal files in the same order and differs for a changed byte, a renamed file, a swapped order;
  handshake rejects each mismatch with its reason; `[Slow]` two processes on one machine connect and exchange a command.

### 18. The server's save (M)
- **Scope:** `meitou-server/players.json`, owner keys, the reserved player 0 on a dedicated server, autosave by rule, save on shutdown
  and on empty. **Measure capture and write time** on large saves first; if a capture exceeds one tick, add the dirty filter here.
- **Keeps:** single-player saves have no `meitou-server/` folder.
- **Tests:** a two-player world saved and loaded gives each player back their squads, purse and selection; a single-player save's file
  list is unchanged.

### 19. A joining player's start (M)
- **Scope:** character creation on the client against a preview, `CreateSquad` checked by the gate, optional import
  (`Sim/Saving/SaveImport.cs`), the HUD's player list.
- **Risks:** the character editor assumes a waiting world (`PlayerState.InCreation`, `Game/Editor/CharacterEditorHost.cs`).
- **Tests:** a `CreateSquad` beyond the start's limits is refused.

### 19a. Trade between players (M)
- **Scope:** the `PlayerTrade` session, its six commands and their checks in the gate, the personal view of an open trade, the trade
  window and the context-menu verb, `GiveMoney`, the `trade` rules, the server's trade log
  ([03](03-commands-and-authority.md#trade-between-players)). In stage B a traded character changes owner; its change of faction comes
  with package 28.
- **Files:** new `Sim/Trading/PlayerTrade.cs`; `Sim/Player/` (purse), `Sim/Factions/SquadManagement.cs` (a character moves between
  owners), the gate, the personal view, `Game/Ui/` (the window), the context menu.
- **Keeps:** single-player shows no trade verb and no window; the slave and animal window (`CharacterTrading`) is untouched.
- **Risks:** a swap applied by half; an item or a character that changes between the last confirmation and the swap; a character traded
  while an order or a job still names it.
- **Tests:** a swap moves items, cats and a character both ways at one tick, and nothing when one check fails; changing an offer clears
  both confirmations; a character in a fight, carried, captive, or a player's last one cannot be offered; a full squad refuses a
  character; `GiveMoney` out of reach is refused unless `trade.remoteMoney` is on; a disconnect closes the trade with everything in place.

### 20. Speed votes; menus that do not pause (S)
- **Scope:** `time.mode` vote, host and fixed; ready speeds on the HUD; menu pause off outside `local`; build and photo mode without
  pause; `pauseWhenEmpty`.
- **Tests:** vote rules as a table (lower applies at once, higher needs all, timeout majority).

### 21. Offline players (M)
- **Scope:** storing and restoring player squads (the new path for `StoredSquad`), the logout and danger delays, captives, reconnect
  within the delay, the group cap's use of the same path.
- **Files:** `Sim/Population/PopulationStored.cs`, `Sim/Factions/Platoons.cs`, `Sim/Population/PopulationRoaming.cs` (exclusions :268),
  `Sim/Population/PopulationOffscreen.cs:49`, `Sim/Hosting/PlayerGate.cs`.
- **Keeps:** single-player never stores a player squad.
- **Risks:** a stored player character must come back with everything (orders are not restored by saves either; decide which of
  inventory, medical, jobs, AI package survive; `StoredSquad` clears path, AI, carry, combat, docs/simulation.md:367-369).
- **Tests:** store and restore keeps name, body, kit, skills, bounties, job list; a squad in a fight is not stored before the delay.

### 22. Interest, priority, budget (M)
- **Scope:** zone buckets at publish, per-client priority accumulators, the byte budget, the join budget, per-zone list updates by
  revision, the leash and presets ([05](05-areas.md#what-is-loaded)), load shedding steps 1-2.
- **Tests:** a client is never sent a character outside its interest; total bytes per second stay under the budget in a crowded scene;
  an own character is never starved.

### 23. Hosting from the menu; Steam friends (M)
- **Scope:** Multiplayer window (open to friends, direct connect, recent), `SteamP2P` transport, the callback shim and accessors in
  `src/Meitou.Core/Steam/`, rich presence, `+meitou_join`. Windows only.
- **Risks:** two-account NAT and relay behaviour is not recorded as verified; cold "Join game" may start Kenshi
  ([06](06-transport-and-join.md#not-checked)).
- **Tests:** the shim's layout (sizes and offsets) as a unit test; the rest is a manual two-account checklist.

### 24. Mods on join; access control (M)
- **Scope:** "play with this server's mods" (restart with `--mods-cfg`), Workshop subscribe prompt, manifest `side` and API version 3,
  client loading of `server` mods' data phase only, `AddCommandCodec`, `AddViewChannel`, whitelist, bans, admins, admin commands.
- **Tests:** a `server`-side mod's system is not added on a client; a mod command without a codec is refused at the client with a
  clear message; a banned identity is rejected before the baseline.

**Checkpoint: friends can play a whole game together, in one faction, hosted from the menu or on a dedicated server.**

## Stage C: players as factions

### 25. A world-owned faction table (L)
- **Scope:** `FactionTable` wraps `FactionRelations` (`src/Meitou.Data/Gameplay/FactionRelations.cs`) and `PopulationData.Factions`
  (`Sim/Population/PopulationData.cs:358,374-380`); every reader goes through it; `IsPlayerFaction(index)` replaces `== PlayerFaction`
  inside the relations code. No runtime faction yet.
- **Keeps:** relation values, events, wars, their hash (`Sim/Aggro/AggroSystem.cs:2961-2962`) and save keys.
- **Risks:** the deepest change; the table is shared between worlds today, on purpose.
- **Tests:** relation queries and events through the table equal the old table's for every pair, on real data (`[Slow]`).

### 26. `Player.Faction` by folder (L, 8-10 pull requests)
- **Scope:** the 276 Simulation places, one folder per pull request in the order Locks 41, Ai 38, Dialogue 33, Construction 18,
  Population 18, Kidnap 17, Production 16, Saving 13, Crime 12, then the rest ([02](02-players-and-factions.md#migration-path), step 7).
- **Keeps:** pins after every pull request; that is the acceptance test.
- **Risks:** a site read wrongly ("the sender's faction" where "the character's" was meant) shows only with two factions.
- **Tests:** per folder, one two-faction scenario for its main rule (a lock owned by faction B is a crime for A; B's guards treat A by
  relation; A's farm is not B's).

### 27. Faction state (M)
- **Scope:** `FactionState` split from `PlayerState`; research and map knowledge per faction; `World.Research` becomes the primary
  faction's; `OwnedProperties` cache per faction.
- **Tests:** research finished by faction A does not unlock buildings for B.

### 28. Runtime factions and diplomacy (L)
- **Scope:** create a faction at join (`newFactionPerPlayer`), the diplomacy table, declare, propose, accept, the alliance notice,
  join and leave a faction, roles, PvP rules, save of runtime factions; a character traded to a player of another faction changes
  faction (package 19a).
- **Tests:** the diplomacy table as a state machine; a hostile declaration makes the two factions' characters enemies to
  `AggroSystem.IsEnemy` and peace ends it; joining moves buildings and squads.

### 29. Raids, crimes and bases per faction (M)
- **Scope:** campaigns over all player factions (`Sim/Population/PopulationCampaigns.cs:307,558-754`), bounty and bail money by owner
  (`Sim/Crime/ArrestSystem.Bounties.cs`), unique-character flags per faction, unattended base protection and production.
- **Tests:** a campaign targets only a town whose own faction stands in it; an unattended base refuses another player's lockpick.

### 30. Hardening (M)
- **Scope:** load shedding steps 3-5, reconnect, a soak run (8 scripted players, hours), fuzzing the decoders, the play checklist
  below, `docs/multiplayer.md` complete.

**Milestone 1 is done when** two to eight people can: start from the menu or a dedicated server; join by Steam or address with the
server's mods; make or import characters; play in one faction or several; trade, fight, build, research; change speed by vote; leave
and come back to their characters and base; and the server can be stopped and restarted from its save.

## After milestone 1 (not planned in detail)

Encryption; hiding unseen sneakers; a server browser; the slim server executable and the move of `Meitou.Modding`; mod file transfer;
per-pair raid managers; incremental saves; local ragdoll playback; several simulators ([05](05-areas.md)).

## Order and dependencies

```
 1 ─ 2 ─ 3 ─ 4 ─ 5 ─ 6          host           (6 needs 1-5)
 7 ─ 8                          commands       (independent of 1-6)
 9 ─ 10 ─ 11                    players        (11 needs 7)
 12, 13 ─ 14, 15                views          (13 needs 9; 14 needs 12, 13)
 16 (needs 7, 13, 14) ─ 17 ─ 18 ─ 19 ─ 19a ─ 20 ─ 21 ─ 22 ─ 23 ─ 24
 25 ─ 26 ─ 27 ─ 28 ─ 29 ─ 30    factions       (25-27 can start any time after 11, beside stage B)
```

The three lanes of stage A (host, commands and players, views) do not touch the same files and can go in parallel. Stage C's packages
25-27 change no behaviour either and can run beside stage B if there are hands for it.

## The risks that could change the plan

| Risk | Seen when | If it happens |
|---|---|---|
| The tick cost on a server CPU is far from the bench line, or does not scale with threads | package 16's harness | Lower the presets; bring load shedding forward |
| Save capture takes many ticks on a large world | package 18 | Dirty filter and sliced capture before anything else in stage B |
| Playback looks wrong (sliding feet, late ragdolls) | package 16 | More animation state on the wire, or local ragdoll playback |
| Steam P2P fails between real accounts behind NAT | package 23 | Friends join by address; a UPnP step |
| The faction table refactor is too invasive for upstream | package 25 review | Milestone 1 ends at the checkpoint; factions become milestone 2 |
| Upstream does not take large refactors (CONTRIBUTING.md: "not accepting large features or new systems") | before package 1 | See [09](09-open-questions.md), Q1 |

## Corrections from the code check (19 November 2026)

Every statement of fact in this file was checked against the code at `a1ef2e06f`. The rows below did not hold as written: where a row
and the text above differ, the row is right. Line numbers are this file's. WRONG: the code does something else. OFF: the right
thing at another place or with another number, or cited lines that do not say it. UNSUPPORTED: no code found that backs it.

| Line | Statement | Verdict | What the code says | Corrected |
|---|---|---|---|---|
| 254-255 | Package 26: "the 276 Simulation places, ... Locks 41, Ai 38, Dialogue 33, Construction 18, Population 18, Kidnap 17, Production 16, Saving 13, Crime 12" | OFF | The counts do not reproduce. `Player\.Faction\b` in `Sim/`: 213 in 80 files; with `PlayerFaction`/`PlayerFactionOf(` forms: 297 in 95 files. Per folder (second pattern): Locks 38, Ai 24 (Aggro 5 apart), Dialogue 26, Construction 19, Population 49, Kidnap 14, Production 16, Saving 30, Crime 10. The order and the sizes of the folders differ from the design (Population and Saving are much larger). | "About 300 places use the player faction in Simulation (`Player.Faction` 213, with `PlayerFaction` 297); by folder, largest first: Population 49, Locks 38, Saving 30, Dialogue 26, Ai 24, Construction 19, Production 16, Kidnap 14, Crime 10 (recount with a stated pattern before planning the pull requests)." |
| 140-148, 33-34 (pkg 1) | Package 1 moves `Advance`/`AdvanceSimulation`/`SimulationTick` (:240-326) out of `WorldSession`, which "keeps camera, input" | OFF | `WorldSession.SimulationTick` (:314-326) sets `World.ViewPosition = ViewEye ?? Camera.Current.Eye` and `World.ShownInteriors` (:320-321) from the camera before `RunTick`; `AdvanceSimulation` also reads `Camera`-less state only after that. The moved tick therefore depends on the camera that stays behind; package 1 does not name it, and package 15 changes it later. | "`SimulationTick` takes the view eye and shown interiors from the camera (`WorldSession.cs:320-321`); package 1 passes them to the host as inputs." |
| 51-52 | Package 3 replaces "the tool's hand copy (`tools/Meitou.Tools/ProbeWorld.cs:261`)" | OFF | `ProbeWorld.MakeWorld` (:261-271) builds a minimal `StandardSystemOptions` (SynchronousPaths, Dialogue, `Population = { Background = false }`, RagdollEnvironment) and takes every rule at its default; it does not copy the gameplay settings. Package 3 replaces it with `WorldRules.Default` rather than a copy. | "`ProbeWorld.cs:261` builds its own minimal options with default rules; it takes `WorldRules.Default`." |
| 216, 215-217 | Package 21's Files include `Sim/Hosting/PlayerGate.cs` | UNSUPPORTED | `Sim/Hosting/` does not exist at a1ef2e06f (packages 1 and 5 create `WorldHost`, `HostClock`, `TickAhead`, `SaveService` there) and no package in 08 creates `PlayerGate` (01 calls it "new"). Package 21 lists it as an existing file to change. | "`Sim/Hosting/PlayerGate.cs` (new in package 7 or 14; name the package)." |
| 121 | `Player.Money` (43 places) | OFF | `Player\.Money\b` in `Sim/`: 39 in 17 files. `Player.Selection` 36 (design 37) and `Player.Squad` 16 (design 16) hold. | "`Player.Money` (39)". |
| 69 | `GameSaves.cs` has "63 writes to simulation state today" | UNSUPPORTED | Not reproducible: no pattern in the file defines a "write to simulation state" to count. | Drop the number or name the pattern used. |
| 144-145, 149 | package 14: "roughly 60 shipping-code places", "268 tick stamps", "781 binding errors" | UNSUPPORTED | 60: see 04 (the table is incomplete). 268 and 781 come from tool runs not in the checkout. `World.Tick` in `Game/` recounts to 322 (`[wW]orld\.Tick\b`). | "at least 75 places; the tick-stamp and binding-error counts come from the inventory tool." |

## As built

| Package | Pull request | Differences from the plan |
|---|---|---|
| 1 | [#1061](https://github.com/VlcekM/MeitouClient/pull/1061) | `WorldHost`, `TickAhead` and `TickRunner` in `Sim/Hosting/`. No `HostClock`: the two clocks are existing types and the host holds them directly. `WorldSession` keeps its members and forwards them, so no caller changed. The session hands the host the eye and the shown interiors before the ticks (`GiveView`), which the plan did not mention. No test added (the proposed one waits for approval); `TickAheadTests` covers the moved code through the session. |
