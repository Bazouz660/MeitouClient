## Async services and tick-boundary inputs: findings

### Verdict

As built, lockstep (L) cannot reproduce the same world on every peer, even with identical commands. The reason goes beyond late async answers. The navmesh world is swapped atomically on a worker thread at real-time moments, and the simulation reads it **synchronously inside parallel tick phases**. The code says so itself: `NavMeshService.cs:16-18`: *"A finished mesh goes live at once, whenever that is … the simulation can see a zone appear in the middle of a tick."* Making answer timing deterministic is not enough for L. The navmesh, door table, world-state snapshot and ragdoll geometry the simulation sees would also have to be **pinned per tick**. Host-authoritative replication (S) needs none of this: the host keeps the current behaviour.

### Inventory

| Service | Thread | Applied when | Sync mode |
|---|---|---|---|
| `PathService` | own thread "Meitou.Sim paths" | `MovementSystem.Inputs` of the first tick after the answer is ready | `synchronous: true` (answered inside `Submit`, applied in the same tick's Schedule) |
| `NavMeshService` (zone builds and rebuilds) | "NavMeshBuilder" thread (+`Parallel.For` per tile) | **immediately, mid-tick** (volatile swap) | none; tests `await nav.LoadZone` before ticking |
| Navmesh unload | host frame | real time, 30 s (`Stopwatch`) | none |
| `NavDoors` | written by the simulation (serial), read by the path thread | whenever the query runs | none |
| `ZoneGate` | simulation thread, reads the live nav world | SlowWorld every 15 ticks | null = always open |
| `RepathCommand` | host frame | `Tick = World.Tick`, when `navChanged` (real time) | `UpdateNav(wait:true)` in screenshots |
| Population builds (towns, roamers, wild) | "Meitou.Sim population" thread | `PopulationSystem.Inputs` of the first tick after ready | `PopulationSettings.Background = false` |
| `RagdollGeometry.Buildings` | `Task.Run` per zone | the tile asks again each `Cover` until gathered | none (goldens pass no ragdolls) |
| `World.PreparePopulation` | host, once per loading frame | outside ticks | n/a |

### 1. PathService (`src/Meitou.Simulation/PathService.cs`)

- **Request:** `MovementSystem.Schedule` (serial, `MovementSystem.cs:167-183`) submits `(CharacterId, From, To, FootprintRadius, WaterFactor)` for each character with `MoveFlags.NeedPath`. IDs come from `Interlocked.Increment(ref nextId)` (`:44`). Schedule is serial, so the ID order is already deterministic.
- **What decides the request:** world state only. `NeedPath` is set by move orders, follow, wander, pursuit, `Reroute` (`MovementSystem.cs:473-478`) and `RepathCommand` (`:87-96`). The goal height, however, comes from a live nav read: `world.Walkability.GroundHeight(...)` (`:177`).
- **Thread:** `Work()` (`:53-56`) calls `IWalkability.FindPath`, which goes through `NavAdapter` to `NavmeshWalkability.FindPath`. That reads the *current* volatile `NavWorld` and the live `NavDoors`.
- **Apply:** `ApplyAnswers` runs at the start of `Inputs` (`MovementSystem.cs:83`). `Drain()` returns everything ready, sorted by ID (`PathService.cs:59-65`). Stale answers are filtered by `cold.PathRequest != answer.Id`, with the async-only `PathApplied` path (`:193-205`, "not hashed", `CharacterTable.cs:68-72`).
- **Sync mode:** `if (IsSynchronous) answers.Enqueue(Answer(request));` (`:46`) and `if (Paths.IsSynchronous) ApplyAnswers(world);` (`MovementSystem.cs:182`). The game selects it with `SynchronousPaths = !interactive || IsDuel` (`GameHost.Saving.cs:64`; wiring at `StandardSystems.cs:157`).
- **What L would need:**
  1. Stamp each request with its tick T. `ApplyAnswers` at tick T+D takes exactly the answers with `RequestTick + D == tick`, and blocks on the worker (or answers inline) if one is missing.
  2. The answer must be a pure function of the request plus a **pinned nav snapshot** (the `NavWorld` adopted at T) plus a **doors snapshot** taken at T. Today `NavDoors.State.Closed` is volatile and "a query that is already running sees it at its next polygon" (`NavDoors.cs:9`).
  3. `FindPath` also records `routeZones[zone] = Stopwatch` (`NavmeshWalkability.cs:199-215`). That is a side effect on the host's load set and must become tick-stamped.
  4. Note that sync mode applies at T while async applies at T+k with k ≥ 1. A fixed D ≥ 1 is therefore a behaviour change from both.

### 2. Navmesh (`Meitou.Navigation`, `NavAdapter`)

- **Request:** `GameHost.UpdateNav` (`GameHost.cs:308-327`) runs every frame (`GameHost.Interactive.cs:432`):
  ```csharp
  foreach (var z in population.ActiveZones.Concat(nav.Walkability.RouteZones(TimeSpan.FromSeconds(30))))
  ...
  if (Stopwatch.GetElapsedTime(seen, now).TotalSeconds > 30) { nav.UnloadZone(z); ...}
  ```
  The inputs are the population's active zones (which follow player characters, see §6) plus the zones that provisional routes lead into (Stopwatch-timestamped). Runtime buildings call `nav.AddRuntimeBuilding` from the host's snapshot diff (`GameHost.Building.cs:129`), which calls `Rebuild` on each reached zone.
- **Thread and apply:** `NavMeshService.Work` (`:94-141`). It loads from the cache or gathers and builds, then `ZoneKeptClosure.Spread`, then `Walkability.SetWorld(world.With(own))` under `publishLock`, at any moment. `ZoneReady` sets `navChanged` (`GameHost.cs:130`). On the next frame `UpdateNav` enqueues `RepathCommand { Tick = session.World.Tick }` (`:325`).
- **In-tick synchronous reads of that live world** (`volatile NavWorld world`, `NavmeshWalkability.cs:16`). Unloaded zones fall back to `OpenGroundWalkability`:
  - `MovementSystem.Move` (parallel): `:307`, `:423-433`
  - `CombatSystem.cs:240-242`
  - `CarrySystem.cs:374`
  - `AnimationSystem.cs:270-271`
  - `AiSystem.cs:491-492,883`
  - `BodySystem.cs:199`
  - `CombatSystem.Ranged.cs:209,576-591`
  - population placement (`PopulationSystem.cs:403-422`)

  Different partitions of one tick can see different worlds.
- **ZoneGate:** `population!.ZoneGate = zone => nav.Walkability.World.Contains(zone);` (`GameHost.cs:174`). It is checked in `Load`/`LoadWild`/`Ready` (`PopulationSystem.cs:243`, `PopulationWild.cs:360`, `PopulationRoaming.cs:43-44`). A town's spawn tick therefore depends on build speed.
- **Door gate:** `NavAdapter : IDoorGate` (`NavAdapter.cs:13-17`). `FixtureRegistry` writes it serially (`:176`, `:332`), so the writes are deterministic, but readers are asynchronous (see §1). `DoorSpotOf` and `NavDoorGeometry.Spot` are synchronous and data-derived, so they are deterministic.
- **Mesh content identity across peers (Unknown):**
  - Whether a cached mesh is bit-identical to a fresh build.
  - Whether the per-tile `Parallel.For` build (`ZoneNavMeshBuilder.cs:73`) is order-independent.
  - `ZoneKeptClosure` claims "same in any load order" for one set of zones. But `Unload` (`NavMeshService.cs:85-92`) only removes the zone from `full` and does not re-prune neighbours, so kept regions look history-dependent. I read this from the code; no test checks it.
  - Golden hashes are pinned only on Windows (`MathF` differences; `GoldenHashTests.cs:74`).
- **Sync mode:** none in the service. The tests preload: `await nav.LoadZone(...)`, then `new PathService(nav.Walkability, synchronous: true)` (`HubNavigationTests.cs:31-35`). Boot blocks on the start zones (`GameHost.cs:244-247`), and save loading waits up to 15 s (`NavmeshAround`, `GameHost.Saving.cs:275-293`).
- **What L would need:**
  1. Split "built" from "adopted". The simulation owns the `NavWorld` it reads and adopts zone Z at the tick `T_request + D_nav`, in a serial phase. All peers block until it is built; a cold build is about 2 s per zone (`GameHost.cs:242`).
  2. Requests and unloads come from simulation state, counted in ticks rather than `Stopwatch`.
  3. `RepathCommand` is raised by the simulation on adoption, not by the host.
  4. Runtime-building rebuilds are keyed to the tick of the simulation event.
  5. `ZoneGate` reads the adopted world.
  6. Peers must provably produce identical meshes (ship meshes, or hash-check them at adoption).

### 3. Population builds (`PopulationSystem*.cs`)

- **Thread:** one worker running a `BlockingCollection<Action>` (`PopulationSystem.cs:69-72`).
- **Requests:**
  - `Load` (towns): `work.Add(() => built.Enqueue(BuildTown(site, loadId, seed, walk, held)))` (`:260`)
  - `LoadWild`: `PopulationWild.cs:377-382`
  - roamers and saved platoons: `PopulationRoaming.cs:413-426`
- **Triggered by:** `RefreshZones` and the roaming/wild steps in `SlowWorld`, gated by `world.Tick % settings.CheckEveryTicks != 0` (15, `:144`).
- **Apply:** `Inputs` drains `built`, sorts by site, and checks `LoadId` and `Status` (`:106-116`). `DrainRoamers` (`PopulationRoaming.cs:437-443`) and `DrainWild` (`PopulationWild.cs:394-401`) are sorted the same way. The sort and LoadId check make the *order* deterministic; the *tick* is not.
- **Non-pure worker inputs (L hazard):**
  - `walk` is the live nav (`IsWalkable` in `Place`/`PlaceInSector`/`BuildNest`).
  - `BuildOne` reads `States` (`PopulationSystem.Build.cs:130`). Those are the live `WorldStates`, whose volatile snapshot `Refresh` swaps every look (`PopulationSystem.cs:89`, `WorldStates.cs:18`).
  - Spawning re-reads the nav at apply time (`PopulationSystem.cs:403-422`).
- **Sync mode:** `Background = interactive && !IsDuel` (`GameHost.Saving.cs:60`). With `work is null`, the build spawns inline in SlowWorld (`:253-257`). `TitleBattle.cs:26` and the tests use it.
- **What L would need:** capture `States` and the pinned nav snapshot at enqueue time, then apply at `T + D_pop` in `Inputs`, blocking if not ready. Simpler: run builds synchronously; they are seed-pure apart from those two inputs.

### 4. Ragdoll building geometry (`RagdollGeometry.cs`)

- **Request and thread:** `RagdollPhysics.Cover` (SlowWorld, serial) calls `MakeTile`/`AddBuildings`, which call `environment.Buildings(...)`. That does `zones.GetOrAdd(zone, z => Task.Run(() => Gather(z)))` (`:72`) and returns `false` until the gather completes, so the tile keeps `BuildingsPending` and asks again (`RagdollPhysics.cs:160-170,212-216`).
- **What decides the request:** the positions of moving ragdolls.
- **State coupling:** "While `Whole`, the character's position follows the root" (`docs/simulation.md` Ragdolls). The docs already admit "the building gather's timing can change a live game's hash". `Evict` is LRU over 16 zones (`:101-106`), so a zone can be gathered again later.
- **Sync mode:** none. Without a host environment the simulation uses `WalkabilityEnvironment` (`Buildings => true`), which is also how tests and goldens run.
- **What L would need:** a gather that blocks on first use (or at T+D), with no eviction-driven variance. Alternatively, keep ragdoll output cosmetic and off the authoritative position.
- Heightmap blocks (`GroundHeight`, `:47-58`) are synchronous file reads, so they are deterministic.

### 5. Loading-time inputs outside ticks

- `World.PreparePopulation(cameraFocus)` (`World.Loading.cs:8-28`) runs population `Inputs`, `RefreshZones` and `Activate` outside a tick. It is called **once per covered loading frame** (`GameHost.Transition.cs:112`), so the number of calls and the drained builds depend on frame timing.
- `SettleNewWorld` calls `session.World.RunTick()` directly (`GameHost.Transition.cs:190-199`).
- L needs a deterministic join instead: the host sends a save or snapshot at an agreed tick, with services in sync mode.
- Related, and outside this area: `World.ApplyPausedInterfaceCommands` applies some commands between ticks while paused (`WorldSession.cs:88`).

### 6. Zone activation and focus

- **Rule** (`PopulationSystem.RefreshZones`, `:200-236`, every 15 ticks):
  ```csharp
  // The player's characters activate the zones round them ...; the camera focus does only without a player.
  if (prev[i].Alive && world.Characters.Cold(i) is { IsPlayer: true }) foci.Add(prev[i].Position);
  if (foci.Count == 0 && focus is { } camera) foci.Add(camera);
  foreach (var f in foci) ZoneActivation.AddZonesAround(f, settings.FastZoneHopping ? 1 : 0, active);
  ```
  - The box is ±340 (`ZoneActivation.cs:17`). The ring is 0 because `FastZoneHopping` is never set by the host.
  - It **already supports many foci**: one focus per living `IsPlayer` character, unioned into one set.
  - Unload grace is 60 s of game time, counted in ticks. The navmesh then follows `ActiveZones` (§2), not the camera.
  - The doc basis is `game-loop.md:29-32` ("Loaded zones near player drive active squad membership").
- **FocusCommand:** a single `Vector3? focus`; the last command wins (`:102`). It is issued by:
  - `GameHost.SendFocus` (`GameHost.Characters.cs:27-35`), every frame from `GameHost.Interactive.cs:442` once the camera target has moved ≥50 units, with `{ Tick = session.World.Tick }`
  - boot (`GameHost.cs:269`) and `SettleNewWorld`
  - the duel (`GameHost.Duel.cs:94`, tick 0)
- **The focus still affects world state when players exist:** `TryAnnounce` requires the campaign leader within 2500 of the *camera* (`PopulationCampaigns.cs:976`). That sets `inst.AnnouncedAt`, which gates campaign progress.
- **For multiplayer:**
  - Remote players' characters must carry `IsPlayer` (or a per-player generalisation) to drive zones. `PlayerState.Faction` is single (`PlayerState.cs:11-12`).
  - Under L, `FocusCommand` must travel in the lockstep stream and become per-player (`FocusCommand(PlayerId, Position)`, with campaign announcements checking any player's focus). Otherwise the no-player case and campaign announcements diverge.
  - Under S, the host must receive each client's camera focus as a command for announcements. Zones and navmesh for every player area come for free, but all zone builds go through one "NavMeshBuilder" thread.

### 7. Deterministic or not world-affecting (no action needed)

- `FoliageResourceNodes` (lock, synchronous), `NavDoorGeometry`, `CoarseRoute` (lazy, `Parallel.For` over independent rows), `WorkerPool`, `AnimationSystem` clip caches: deterministic.
- `BuildingFootprints` (`Task.Run`) is used only by the host's `PlacementCheck`; the simulation "trusts it" (docs), so under L the placing peer's command carries the decision.
- Output-only queues: `Floaters`, `BloodMarks`, `Sounds`, `LimbsChanged` (drained serially in Inputs), save tasks, audio, particles, the navmesh overlay.

### Recommendation

Under S, every hazard above stays on the host, which already handles them correctly for one machine. The work there is per-player `IsPlayer`/focus plumbing.

L needs at least these changes:
- a tick-pinned `NavWorld` and door snapshot read by every in-tick walkability call
- tick-delayed, blocking adoption of zones, paths and population builds
- tick-based nav unload and `RouteZones`
- `RepathCommand` raised by the simulation
- deterministic ragdoll geometry
- a deterministic join in place of `PreparePopulation`
- proof that navmesh builds are identical on every machine (Unknown today)