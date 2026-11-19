# 01. The world host

What runs a world when there is no window, and what has to move out of the game to make it. Upstream `a1ef2e06f`.
`Sim/` = `src/Meitou.Simulation/`, `Game/` = `src/Meitou.Game/`. Terms: [overview](00-overview.md#terms).

## Today (facts)

The simulation itself needs nothing from the game: `WorldBoot.Load` reads levels, navmesh, rigs and animations "with no window, GPU or
audio" (`Sim/WorldBoot.cs:46,115`), `StandardSystems.Build` wires the systems (`Sim/StandardSystems.cs:355`), `World.RunTick` runs one
tick (`Sim/World.cs:798`). What a *running* world needs beyond that lives in the game:

| Duty | Where it is today | Game-only parts |
|---|---|---|
| Clocks and the tick loop | `WorldSession` (`Game/Host/WorldSession.cs:20`): `Advance` :240, `AdvanceSimulation` :254, `SimulationTick` :314-326, tick-ahead thread `Meitou.Tick` :418-568 | The same class owns `CameraRig` and `InputState` (:107-120); its constructor takes the GPU terrain's height function (`Game/GameHost.cs:647`) |
| Inputs copied into the world before each tick | `GameHours`, `Daytime`, `PhysicsSpeed`, `ViewPosition`, `ShownInteriors` (`WorldSession.cs:316-321`); `NearRadius` from the character renderer (`GameHost.cs:658`); `Weather` each frame (`Game/Host/WorldSync/WeatherFeed.cs:56`) | `ViewPosition` is the camera eye; `NearRadius` is a draw distance |
| Zone gate and navmesh streaming | `GameHost` region "Nav streaming" (`GameHost.cs:1334-1483`), gate codes :1485-1537; `PopulationSystem.ZoneGate` assigned at five sites (`GameHost.cs:687`, `Game/Host/GameSaves.cs:500`, `Game/Host/MetalGameLoop.cs:851`, `Game/Title/NewGameLaunch.cs:46,123`) and copied in a tool (`tools/Meitou.Tools/ProbeZoneStreamer.cs:93`) | None: it only touches `NavSystem`, `PopulationSystem` and commands |
| Weather schedule | `WorldWeather.Update(eye, hour, sunHeight, held)` (`Game/Rendering/Sky/WorldWeather.cs:108`), called from the draw (`Game/Rendering/Frame/WorldFrame.Draw.cs:502`); the `WeatherWorld` is created by the GPU builder (`Game/Rendering/Frame/WorldGpuBuilder.cs:238`) | Sky, fog, wetness and haze follow the camera region (`Data/World/WeatherWorld.cs:211,279`) |
| Weather effect zones (dust, gas, burning) | Collected each frame from the renderer's live particle units (`WeatherFeed.cs:53-72`) | Entirely: without a renderer there are none |
| System wiring from settings | `GameSaves.BuildSystems` (`GameSaves.cs:677-723`), `ApplyGameplay` (:651-672), live callbacks in `Game/Host/GameLoop.cs:503-549` and again in `MetalGameLoop.cs:1320-1351`; copied by hand in `ProbeWorld.MakeWorld` (`tools/Meitou.Tools/ProbeWorld.cs:261`) | Reads `UserConfig` |
| Saving | `GameSaves`: requests as fields :31-48, `Autosave` :99-116, `PrepareBase` on a task :87-91, `QuickSaveNow` :192 (`SettleAhead` :199, capture :219, write task :238-254), `SwapInSave` :467-549 | Camera view, sky fields, blood decals, notices, loading screen, `UserConfig` (autosave index) |
| Events out of the world | Queues the host installs and drains: sounds, floaters, blood marks, block sparks, messages, said lines, knock-outs, town and wild logs ([04](04-replication.md#events)) | Consumers only |

There are two frame loops that each repeat this wiring: `GameLoop` (Vulkan) and `MetalGameLoop` (`Game/Host/MetalGameLoop.cs:786-804`).
`--headless` (docs/launch-options.md:133) has no window but still draws offscreen, so it needs a GPU. There is no server entry point.

Weather is closer to ready than the research said: each region's `Update(WeatherTime)` needs only game time
(`Data/World/WeatherRegion.cs:165`), the regions already update without the camera (`WeatherWorld.cs:214`), and what the simulation reads
(`IWeatherSource.At`) uses only the region under a point (`WeatherFeed.cs:33-40`). The camera drives presentation only. Real time enters
as a stopwatch that turns frames into game minutes (`WorldWeather.cs:111-124`). The weather seed is the constant 1, not the world seed
(`WorldGpuBuilder.cs:26`).

## Target (proposal)

### `WorldHost`

A class in a new slice `Sim/Hosting/` (namespace `Meitou.Simulation.Hosting`). It references no Game type. It owns:

| Part | Proposed class | Comes from |
|---|---|---|
| World, systems, their disposal, `ReplaceWorld` | `WorldHost` | `WorldSession` :147-195 |
| `SimulationClock`, `GameClock`, the tick loop, `TickMs` | `HostClock` | `WorldSession` :71-84, :240-357 (the clock types are already in `Sim/Session/`) |
| Tick-ahead thread | `TickAhead` | `WorldSession` :366-568; used only when a local renderer asks for it |
| Zone gate, navmesh wanted set, stale drop, repath, gate codes | `ZoneStreamer` | `GameHost.cs:1334-1537` and the tool's copy |
| Weather regions on the game clock, effect zones | `HostWeather` | `WeatherWorld`'s region half; `WeatherFeed.HostWeather` |
| Rules to system options, live rule changes | `RulesBinder` | `GameSaves.cs:651-723`, `GameLoop.cs:503-549` |
| Save requests, autosave timer, base copy, write worker, load | `SaveService` | `GameSaves.cs:54-116,192-254,318-549` minus the camera and UI |
| Players, connections, the command gate, per-player publish | `PlayerGate` | new; [02](02-players-and-factions.md), [03](03-commands-and-authority.md) |
| Event taps | `HostEvents` | the queues of [04](04-replication.md#events) |

`WorldSession` stays in the game as the *local client*: camera, input, bindings, the interpolation alpha and the two shown snapshots. It
holds a `ClientSession` (below) instead of a `World`.

The inputs the host copies into the world lose their camera:

| Input | Today | With a host |
|---|---|---|
| `GameHours`, `Daytime` | from the session's `GameClock` | from `HostClock` |
| `PhysicsSpeed` | the session's speed | `HostClock` speed |
| `ViewPosition` (one eye) | camera eye | the players' view anchors, a list ([05](05-areas.md#step-rate)) |
| `NearRadius` | renderer's draw reach | a rule (`simulation.nearRadius`, default 8000; `Sim/World.cs:629`) |
| `World.View` (`CameraViewCommand`) | one camera frustum | one per player ([05](05-areas.md#step-rate)) |
| `ShownInteriors` | the local camera's shown interiors, read by ambient dialogue (`Sim/Dialogue/DialogueSystem.Ambient.cs:154-158`) | null on a server (speech is filtered per client when sent); unchanged for a lone local player |
| `Weather` | renderer's schedule and particle zones | `HostWeather` |

### Weather without a camera

- `HostWeather` owns the `WeatherWorld` regions and steps them from the game clock, minute by minute, as `WorldWeather.Update` does now.
  It implements `IWeatherSource.At` with `RegionAt`.
- The client keeps `WorldWeather` for presentation (sky, fog, wetness, particles). It no longer schedules: it receives each region's
  season, weather entry, strength and wind when they change (a few bytes per region per change) and renders the region under its camera.
- **Effect zones** need a decision. Today they are the renderer's live particle units, so they exist only round one camera.
  Proposal: the host derives them from the same data without particles: for each loaded zone, the placed effect emitters and the
  region's current weather effect, with the effect's `effect radius` and strength. **Not checked:** how placed and weather-spawned
  effect units are positioned (`Data/Particles/`), so whether this can match what the renderer spawns. Until that is read, the first
  milestone keeps region weather (acid rain, wind, wetness) authoritative and treats local effect zones as an open item
  ([09](09-open-questions.md), Q12).
- Seed: keep 1 for old saves' sake; a new server world may use the world seed. No behaviour depends on which, since the schedule is saved
  (BIOMES records, docs/formats/save.md).

### The client session

```
interface IClientSession            // proposal; lives in Simulation so tools and tests can use it
{
    WorldSnapshot Current, Previous;   // what to draw; same contract as today (valid until two more publish)
    float Alpha;                       // interpolation between them
    long ShownTick;
    void Send(SimCommand command);     // stamps nothing: the host stamps sender and tick
    ClientEvents Events;               // sounds, floaters, blood, messages for this viewer
    ServerInfo Info;                   // rules, speed, vote state, players, ping
}
```

- `LocalSession`: wraps a `WorldHost` in the same process. Snapshots are the host's objects, by reference. `Send` enqueues directly.
  This is single-player and the hosting player of a co-op game.
- `RemoteSession`: decodes the network stream into `WorldSnapshot` objects ([04](04-replication.md)).

The game talks to `IClientSession` only. Developer tools (console, agent channel, debug menu, scenarios) keep direct `World` access through
`LocalSession.Host`, which is null on a remote client; their entry points are disabled there.

### Three ways to run

| Mode | Process | Host | Local player | Transports listening |
|---|---|---|---|---|
| Single-player | game | internal | yes | none |
| Open to friends | game | internal | yes | Steam P2P and/or UDP, chosen in the menu |
| Dedicated | `MeitouClient --server [--load <save>] [--config <server.json>]` | alone | none | UDP |
| Joined | game | none | remote | - |

`--server` branches in `Game/Host/Program.cs` before the splash, Steam and Vulkan (which today are already skipped for `--headless`,
`Program.cs:78-80`). It boots data and code mods as the game does, builds a `WorldHost`, reads commands from standard input and from the
existing agent channel (`--control-dir`, `Game/DevTools/Agents/AgentChannel.cs:18`), and never creates `GameHost`.
A dedicated server needs the original game's files (data, levels, collision, animations): `WorldBoot` reads them. Whoever runs one must
own Kenshi ([09](09-open-questions.md), Q7).

Later (not milestone 1): a `Meitou.Server` executable that references Simulation and a `Meitou.Net` library but not Game, once the mod
contract has moved ([07](07-settings-mods-saves.md#code-mods)). CLAUDE.md already plans that move ("`Game/Mods` keeps `Meitou.Modding`
until the mod contract moves to Simulation").

### Threads

| Thread | Single-player today | Internal server with friends | Dedicated |
|---|---|---|---|
| Main | frame loop, `Session.Advance` (`GameLoop.cs:1485`) | same | host loop: sleep to the next tick, run it |
| `Meitou.Tick` | ticks during the draw (docs/simulation.md:45-59) | same; the network encoder reads the snapshot the tick published | not used: the host loop *is* the tick thread |
| `Meitou.Sim {i}` workers | cores/2 clamped 1-8 (`GameHost.cs:639`) | same | rule `simulation.threads`, default cores - 2 |
| `Meitou.Sim paths {i}` | cores/4 clamped 2-4 (`Sim/Movement/PathService.cs:72`) | same | same |
| Population build, nav build | as today (`PopulationSystem.cs:158`, `Sim/Navigation/NavMeshService.cs:120`) | same | same |
| Network I/O (proposal) | - | one thread: sockets, Steam callbacks pump | same |
| Replication encode (proposal) | - | one task per connected client per send, on the thread pool, reading only the published snapshot | same |
| Save write | thread-pool task (`GameSaves.cs:238`) | same | same |

Rules that keep this safe:
- Nothing but the tick thread touches `World` between a tick's start and end (today's rule, docs/simulation.md:52).
- Incoming commands are queued from the network thread (`CommandQueue.Enqueue` is already thread-safe, `Sim/Commands.cs:76`).
- Replication reads only published snapshots and the events the host copied out at the tick's end. A published character stays valid
  until two more snapshots publish (docs/simulation.md:41), so an encoder must finish within one tick or copy what it needs first.
  The encoder copies the per-character fields it sends into its own baseline buffers as it reads them, so it never holds a snapshot
  object across ticks.
- A dedicated server ticks on a fixed real-time schedule (33.3 ms at 1x). When a tick overruns, the clock's existing rule applies:
  owed ticks beyond the budget are dropped and the world runs slow for everyone (`AchievedSpeed`, docs/simulation.md:13). That is the
  server's time dilation, with no new mechanism.

## What must not change in single-player

- Tick order, the tick-ahead contract, the snapshot ring.
- `GoldenHashTests` build worlds without `WorldSession` or `GameHost` (`tests/Meitou.Tests/Simulation/SyntheticTown.cs:195-203`), so moving
  host code cannot move a pin as long as `World` and the systems are untouched.
- Loading: the host keeps `World.PreparePopulation` and `StepLoadingPause` (`Sim/World.Loading.cs:14,49`) for a local player's loading
  screen. A server never shows one and never holds the world for a joining player: the joiner waits on its own loading screen while the
  world runs. No loading pause is brought back.
- The Metal loop must go through the same host, which also removes its copy of the wiring.

## Not checked

- Whether `WorldWeather.Update` on the render thread is synchronised with the tick thread's weather reads today.
- Boot time and memory of a windowless start (docs give only the game's: menu at 17.5-18 s with 655 mods, docs/engine.md:50).
- Whether `ModHost` runs with no `PlayerInterface` beyond what `mod-check` exercises (`tools/Meitou.Tools/ModCheck.cs:140`).
