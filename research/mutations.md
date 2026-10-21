## Host-side world mutations outside `CommandQueue`: audit for lockstep (L) vs state replication (S)

### 1. How commands reach the world and how their tick is chosen

- **UI path.** `PlayerInterface.Say` (PlayerInterface.cs:56-60) plays a sound, then calls `session.World.Commands.Enqueue(command)`. The class doc says it "never touches the simulation's state" (PlayerInterface.cs:12-13), and that holds: about 70 distinct command types go out through `Say`, and I found no direct `Inventory`, `Selection`, `Money`, `Relations` or `Buildings` writes in `PlayerInterface*.cs`, `Building/*` or `Ui/*`.
- **Host enqueues directly** at GameHost.cs:258 (`FinishCreation`), GameHost.cs:325 (`RepathCommand`), GameHost.Characters.cs:32 (`FocusCommand`), GameHost.Editor.cs:33, GameHost.DebugMenu.cs:58-85 (the `Debug*` commands), GameHost.Sandbox.cs:67/121/129/148/171-172, GameHost.Duel.cs:94-96, GameHost.Screenshot.cs:34-103 and TitleBattle.cs:34-35 (TitleBattle has its own world).
- **Tick choice.** Almost every command is stamped `{ Tick = session.World.Tick }` (e.g. PlayerInterface.cs:65, 69). `World.Tick` is the number of ticks already run, so the command applies at the very next tick: no input delay. The exceptions are fixed:
  - Duel uses `Tick = 0` and `DuelOrderTick` 45 (Duel.cs:94-96).
  - TitleBattle uses `i * 7`.
  - The editor stamps one shared `tick` for a whole batch (PlayerInterface.Editor.cs:300-306).
  - Steering re-sends `MoveOrder` at most once per world tick while the right button is held (PlayerInterface.cs:212-218).
- **Queue semantics** (Commands.cs:30-69):
  - Ordering is `(Tick, Sequence)`, where `Sequence` is a per-process counter (Commands.cs:44, 64).
  - `SimCommand.Tick`: "a command for a tick already run applies at the next one" (Commands.cs:12). A late command is shifted silently, not rejected.
  - `World.TakeDue` puts in-tick `Defer`red commands ahead of the host's (World.cs:251-265). Those are deterministic.
- **Paused path.** `WorldSession.AdvanceSimulation` calls `World.ApplyPausedInterfaceCommands()` on every paused frame (WorldSession.cs:88). It applies a filtered subset at the current `Tick` without advancing the tick (World.Interface.cs:9-11): Select, Inspect, InspectBuilding, MoveInventoryItem, ArrangeInventory, Buy, Sell, CloseTrade, DialogueChoice, EndConversation, PlaceBuildings, CancelSite, PauseSite, EditCharacter and FinishCreation. This is a sub-tick mutation point whose timing comes from frames.
- **Implicit actor.**
  - No `SimCommand` carries a player id (Commands.cs:10-14).
  - An empty character list (e.g. `MoveOrder([], target)`, PlayerInterface.cs:69) means "the selection", and the selection lives in the single `World.Player` (PlayerState.cs:8-37). `Selection`, `Money`, `Faction` and `Squad` are hashed (PlayerState.cs:98-108).
  - The Simulation project has 114 references to `world.Player.*` in 36 files, plus 164 uses of `IsPlayer`.

### 2. Mutations that bypass `SimCommand`

Classes: **a** = load or new-game setup, **b** = during play from the UI, **c** = debug or sandbox only, **d** = presentation only.

#### Clock, speed, pause

| Site | Mutation | Class |
|---|---|---|
| WorldSession.cs:138-143 | Pause and speed keys: `Simulation.TogglePause/SetSpeed/Faster/Slower` | b |
| PlayerInterface.Hud.cs:64-65 | HUD buttons: `session.Simulation.TogglePause()` / `SetSpeed(...)` | b |
| PlayerInterface.Faithful.cs:17 | `session.Simulation.RequestedPause = settingsOpen \|\| faithfulScreens.HasModal`: any modal screen pauses the world | b |
| WorldSession.cs:95 | `World.GameHours = Clock.TotalHours;` before every tick. The clock belongs to the host (`GameClock`), not the world, and `GameHours` is "not part of the state hash" (World.cs:154-157) | input (every tick) |
| GameHost.Interactive.cs:97 | Settings slider `setHour: v => { session.Clock.SetHourOfDay(v); ... }`. This moves the AI's hour windows | b (settings menu, reachable in play) |
| GameHost.NewGame.cs:62; GameHost.Saving.cs:229 | `session.Clock.SetTime(1, 13)`; `SetTime(loaded.Clock.Day, ...)` | a |
| GameHost.Duel.cs:153-158, 169 | `SetTime`, `Simulation.ResetPhase()`, `Clock.HoldHourOfDay(duelHour)` | c |

#### Simulation rules taken from local config

| Site | Mutation | Class |
|---|---|---|
| GameHost.Interactive.cs:113-120 | Live writes to `CombatSystem.AttackSlots`, `.LateDefences`, `LockSystem.DoorsCloseBehind`, and `PopulationSystem.KeepRoamers/OffscreenFights/OffscreenHealing/CampaignAncestorsOnce/KeepWild` | b |
| GameHost.Blood.cs:52 | `world.Luckyren = config.LuckyrenMode;` every frame. It changes the run: a burst body drops its gear through `world.GroundItems.Drop` (BodySystem.cs:184-200); doc: "Unlike the marks it changes the run" (World.BloodMarks.cs:36-40) | b |
| GameHost.Saving.cs:50-73 (`BuildSystems`) | Systems built from config: the rules above, plus `Background = interactive && !IsDuel` (line 60) and `SynchronousPaths = !interactive \|\| IsDuel` (line 64) | a |

#### World setup and replacement

| Site | Mutation | Class |
|---|---|---|
| GameHost.cs:252 | `population.StartPlayer(session.World, start)` (`--new-game`) | a |
| GameHost.NewGame.cs:23-28, 57 | `session.ReplaceWorld(built.Systems, world => ... StartPlayer(world, chosen))` | a |
| GameHost.Saving.cs:206-213 | `ReplaceWorld` + `SaveLoader.Load(world, ...)` + `world.Player.Selection.Clear(); .Add(squad.Members[0])` | a |
| GameHost.Transition.cs:176-185 (`SettleNewWorld`) | `SendFocus()` + `session.World.RunTick()`, a tick that bypasses the session clock | a |
| GameHost.Editor.cs:47 | An extra `session.World.RunTick()` after character creation | a |
| GameHost.Transition.cs:112 | `session.World.PreparePopulation(session.Camera.Current.Target)` on every covered frame. It mutates population: `focus = cameraFocus`, `Uniques.SeedPrisoners`, `RefreshStates`, `Inputs`, `Activate` (PopulationSystem.Loading.cs:9-27). The number of calls depends on frame timing and the background worker | a |
| WorldSession.cs:41, 110, 114 | `Fixtures.Doors` gate, carrying `Floaters` across the swap, `gate.ResetDoors()` | a |
| GameHost.cs:174; NewGame.cs:26/60; Saving.cs:223 | `population.ZoneGate = zone => nav.Walkability.World.Contains(zone)`. Platoon and town spawning waits for the navmesh (PopulationSystem.cs:243, PopulationRoaming.cs:44) | set at a, evaluated in play |

#### Asynchronous engine inputs during play (the main lockstep blocker)

| Site | Mutation |
|---|---|
| GameHost.cs:308-327 (`UpdateNav`, called at Interactive.cs:432) | `nav.LoadZone` / `nav.UnloadZone`, with zones idle for 30 s of real time (`Stopwatch`) unloaded. When the nav thread finishes, the host enqueues `RepathCommand { Tick = session.World.Tick }`, so the tick depends on that thread's timing. |
| GameHost.Building.cs:68-130 (called at Interactive.cs:434) | Snapshot diff calls `nav.AddRuntimeBuilding` / `RemoveRuntimeBuilding`. The navmesh the simulation queries is changed by the host, not by the simulation. |
| NavAdapter.cs:9-10 | A zone that is not loaded yet answers with the open-ground stand-in, so path results depend on load timing. |
| GameHost.Saving.cs:60, 64; PopulationSystem.cs:71; PathService.cs:32 | Population is built and paths are answered on background threads during play. |
| GameHost.cs:133; RagdollPhysics.cs:255 | Ragdoll building geometry is read on a worker: `Buildings()` returns "False while some of them are not known yet". |
| docs/simulation.md:39-40 | "Async paths, navmesh and population results enter at tick boundaries; arrival timing is not repeatable in play. Determinism tests use synchronous services." The duel gets bit-identical laps only by forcing synchronous services (Saving.cs:63-64; Duel.cs:119-125, 179-180). |

#### Debug, sandbox and tools (c)

- **Debug menu.** Already sends `SimCommand`s (DebugMenu.cs:58-85, DebugCommands.cs). It does read the live `world.Player.Selection` (DebugMenu.cs:51-52).
- **Sandbox.** `population.SpawnSandbox(world, ...)` (Sandbox.cs:56-64) and `SpawnCrowd` (Sandbox.cs:98-112), also run in-game with `--crowd` (GameHost.cs:262).
- **Duel.** Headless `world.RunTick()` laps (Duel.cs:102-137) and `ReplaceWorld` (Duel.cs:149).
- **Screenshot mode.** Scripted enqueues (Screenshot.cs:34-103).

#### Presentation only (d)

- **Floaters.** `world.Floaters` set and drained (PlayerInterface.Floaters.cs:29, 37-44; Floaters.cs:11: "Display only").
- **Sounds.** `world.Sounds = soundQueue` (GameHost.AudioCharacters.cs:64-71; World.Sounds.cs:57-61: "cannot change a run").
- **Blood marks.** `world.BloodMarks` (GameHost.Blood.cs:56-59, 94-106).
- **Logs and notices.** `world.TakeMessages()`, `carry.TakeKnockouts()`, `population.TakeTownLog/TakeWildLog` (GameHost.Interactive.cs:661-676), and `world.Prospected` polled for the results window (PlayerInterface.Prospecting.cs:44-60).
- **Title screen.** `TitleBattle` is a separate world with Seed 173 (TitleBattle.cs:10-40).
- **Camera.** SquadTour, Cinema and the camera never touch the world, except that the camera target becomes a `FocusCommand` (GameHost.Characters.cs:27-35; Interactive.cs:442). There is one `focus` field (PopulationSystem.cs:38, 102), and it only drives zones when there are no player characters (PopulationSystem.cs:202-211).

#### Related state outside `World`, and live reads

- **Relations live outside `World`.** The live `FactionRelations` table is in `PopulationData` (FactionData.cs:125-132: "This is the live table of a game"). It is shared by every world built from that data (StandardSystems.cs:155-201) and mutated by dialogue (DialogueSystem.Effects.cs:108) and by save loading (SaveLoader.cs:185). A resync or late-join payload must therefore carry it. I found no reset on new game or main menu (**Unknown** whether that is intended).
- **The UI reads the live world, not the snapshot.** This matters for (S), not (L):
  - `session.World.Player.Money` (PlayerInterface.Ui.cs:106)
  - `session.World.CanTalk` (PlayerInterface.cs:133)
  - relations through `popData` (GameHost.cs:215-224)
  - `Player.Money` and `CreationRaces` (PlayerInterface.Editor.cs:276, 288)
- **Building placement is half-checked.** The simulation checks only the data half of the placement rules; "the geometric half is the host's" (ConstructionSystem.cs:33-34). Under (S) the host must re-check geometry.

### 3. What lockstep needs converted, and effort

The effort figures are my own estimates.

1. **Command envelope.**
   - Add `PlayerId` and a per-player sequence number.
   - Stamp `Tick = World.Tick + inputDelay`.
   - Order by `(Tick, PlayerId, Seq)` instead of the local `Sequence`.
   - In multiplayer, a late command must stall or be rejected, not shift to the next tick (Commands.cs:12).
   - Effort: 2-3 days.
2. **Per-player `PlayerState`.** Selection, Inspected, OpenContainer, DoorTarget, Squad, Money, Faction and CreationRaces, plus the "empty list = selection" rule. That means 114 `world.Player` references in 36 files, and `IsPlayer` / a single `PlayerFaction` (PopulationData.cs:176). (S) needs this too. Effort: 2-4 weeks.
3. **Pause and speed.** Make these control messages applied at an agreed tick (host-only, or a vote) instead of `SimulationClock` writes (WorldSession.cs:138-143, Hud.cs:64-65). The modal pause (Faithful.cs:17) has to become local-only in multiplayer. Effort: 2-3 days.
4. **Paused interface commands** (WorldSession.cs:88, World.Interface.cs). Disable them in multiplayer, or give each paused application an agreed index. Effort: about 2 days.
5. **Game clock into `World`.** Derive `GameHours` from the tick plus a base, and hash it. `SetHourOfDay` (Interactive.cs:97) becomes a command or is disabled; `SetTime` on load and new game comes from the shared save. Effort: 2-3 days.
6. **Session rules.** AttackSlots, LateDefences, DoorsCloseBehind, the five offscreen toggles and Luckyren become host-owned session rules, sent at start or as a `SetRules` command (Interactive.cs:113-120, Blood.cs:52, Saving.cs:58-67). Effort: 1-2 days.
7. **`FocusCommand`.** Make it per player, or ignore it in multiplayer and let the players' characters drive zones. Effort: about 1 day.
8. **Asynchronous inputs (largest and riskiest).**
   - Navmesh zones must load and unload from simulation state on game ticks, not from wall clock or camera.
   - Runtime buildings must enter the navmesh at a deterministic tick, and `RepathCommand` must be issued by the simulation, not by the nav thread.
   - Each result from the path service, population worker or ragdoll geometry must either run synchronously or be applied at an agreed tick T+K, with every peer stalling until it is ready.
   - **Unknown:** whether the navmesh built from the same zones is bit-identical across machines. This needs a test.
   - Effort: 3-6 weeks.
9. **Load and new game.**
   - The host ships the save, seed and rules. Every peer runs `SaveLoader.Load` / `StartPlayer` and the same settle ticks (Transition.cs:180, Editor.cs:47).
   - The frame-paced `PreparePopulation` loop (Transition.cs:112) must be replaced by a deterministic prepare-to-completion.
   - The character-creation flow must be per player.
   - Effort: 1-2 weeks.
10. **Gating.** Disable sandbox, duel, crowd and screenshot modes in multiplayer, and restrict the debug commands to the host. Effort: about 1 day.
11. **Desync checks.** Exchange `World.StateHash` periodically (World.cs:293-312). Add `GameHours` to it, and include `PopulationData` relations in any resync.

**Totals.**
- Items 1, 3-7 and 10 are about 1.5-2 weeks.
- Item 2 is 2-4 weeks and is needed by both architectures.
- Item 8 is 3-6 weeks and carries the most risk.
- Item 9 is 1-2 weeks.

That is roughly 2-3 months for robust lockstep.

**Contrast with (S).** Host authority avoids items 4, 5, 8 and most of 9, because the host keeps its async services. Instead it needs:
- replication of the snapshot and the drained event queues (floaters, sounds, blood marks, messages, knockouts, prospecting results, trade and dialogue views);
- snapshot fields for the UI's live reads of `World` and `popData`;
- host-side re-checking of building geometry.