# Engine architecture

Projects: Core installation; Data clean-room readers/typed game data; Simulation headless world;
Navigation own navmesh; Engine WorldSession/time/input/camera; Rendering Vulkan/content/streaming;
Rendering.Display window/device/presenter; Game host/UI; ModelViewer debug world/atlas; Tools surveys.
GPU and simulation ownership stay separate. [Simulation contracts](simulation.md),
[native renderer](renderer-native.md), [viewer controls](viewer.md).

## Loop, input and camera

WorldSession owns world, clocks, input/bindings, camera and last two snapshots. Control30Hz real
(default config tickRate), max10 ticks/frame; simulation1/30 game second, speed0/1/2/5 and
6*speed budget. Control precedes simulation; interpolation uses each clock's alpha, shortest
angle rotation and cuts on mode changes. The authoritative time/order/determinism details are
in simulation. Host copies camera state into WorldCamera and snapshots into CharacterDrawList.
Pause/speed input (control-tick keys, HUD buttons) is a `ClockRequest` through `WorldSession.RequestClock`:
applied to the SimulationClock, or handed to `ClockAuthority` (net sessions) when set, which also stops modal
screens pausing (`ModalsPause`). `SimulationTicked`/`WorldReplaced` fire after each tick/world swap.

KenshiCamera follows pivot/boom/zoom/pitch/ground constraints from [camera](formats/camera.md).
Engine choices: pivot500*clamp(boom/600,0.1,1), keys1.5rad/s, mouse0.005rad/pixel; terrain-ground
pivot rather than building support. Free-camera ; follows smoothed flight, exits at ground hit.
InputState consumes events once per tick; commands apply at world boundaries, not UI callbacks.
meitou.user.json persists fps/vsync/fullscreen/tickRate/simThreads/graphics/bindings (layout and versioning: [launcher](launcher.md#graphics)); renderer
settings and current key list belong in viewer. Quicksave F5/loadF9; cinematic F6/hideUI F7.

## Host and loading

GameHost partials own boot/interactive/offscreen, characters, saving, transitions and duel;
PlayerInterface owns picking/HUD/panels/commands. StandardSystems supplies order. NavAdapter
bridges Navigation; never let UI/rendering duplicate simulation state.
GUI executable attaches a caller's console; folder prompt creates its own when necessary.
Native splash precedes Vulkan; worker loading and render-thread GPU steps keep the window
responsive. On Wayland the window is shown and its first configure taken before the first loading frame is presented: a buffer attached before the shell role makes the compositor drop the client (**Verified** natively on KDE Wayland, GLFW 3.4). Scene transitions draw loading between stages, wait on asynchronous results, and
preserve world on failure. Gameplay warm-up waits for nearby population, terrain, scenery, character
assets and used textures (including deferred loads), then <40ms frames for0.5s (max5s for frame pacing).
After 5s of continuous `VramGuard` pressure, content it holds back is no longer waited for (it cannot load until the use falls).
Population builds are queued/applied at frame boundaries without advancing game time, AI or movement;
navmesh-gated spawns are retried while loading. Warm-up and the flight hold the simulation paused.
The Meitou title waits for terrain, scenery, character assets and used textures (including deferred loads), then
one full second of <40ms frames (max5s of ready content for frame pacing, so slow GPUs reach the menu). Its menu artwork/fonts/pipelines warm under the opaque cover too;
streaming uploads retire and a compacting GC completes before that final smooth run.
**GC:** the stock runtime (server GC, four heaps) in builds and release zips alike; the startup `gc` line names the collector (`GcName`). The
experimental Satori GC (github.com/VSadov/Satori, `PublishWithSatoriGC` SDK) is not used in official builds. **Observed** earlier A/B (RTX 4070, 10.0.13
Satori vs stock 10.0.12 fast flight): longest GC pause 15-21 ms stock vs 1-18 ms Satori, frames unchanged (long frames are foliage updates, docs/viewer.md).
**Pooled buffers** (allocation is the GC lever): streaming garbage is mostly LOH arrays from worker jobs. **Observed** in flights: gen0/gen1 pauses under
~40 ms; single blocking gen2s (`NonConcurrentGC`, reason `AllocSmall`) of 0.3-1.7 s in some runs despite SustainedLowLatency, with or without pooling;
what triggers them is **Unknown**. Pooled now:
DDS file bytes of `WorldTextureCache.Load` (`DdsReader.ReadKept(..., pool)`, `ArrayPool<byte>.Shared`; returned in `Load` after an RGBA8 decode or
by the last upload step of a stored BC upload), the GI terrain grid's samples (one array per `TerrainGrid`), the card trimmer's clip/hull scratch
(`FoliageCardTrimmer.Hull`) and the grass generator (`[ThreadStatic]` `FoliageRandom`, reseeded). Contract: a rented array may be longer than asked,
so its length is never the logical size (pass counts; `DdsFile.TrailingBytes` is meaningless on a pooled file); return it in a `finally` or at the
one point after its last reader, never while anything keeps it; a buffer that is dropped instead (a texture never pumped) just goes to the GC.
`ArrayPool<byte>.Shared` rounds up to a power of two (a 4.2 MB file rents 8 MB) and trims idle arrays on gen2; textures in flight bound what it holds.
**Observed** (swamp, 1920x1080, 3000-frame flight, 4 alternating pairs, 2026-10-21): allocated 6.2 GB to 4.8 GB (2.1 to 1.6 MB a frame), gen0+gen1
45 to 35 in total, gen2 count unchanged; frame percentiles unchanged within noise.
Not pooled, kept by their owners: `HeightWindow` arrays (read through `HeightSnapshot` on workers), impostor atlas levels (`ImpostorTextures`
holds the atlas), grass blade arrays (kept as `PendingBlades`). How to rank allocation sites: docs/bench.md.
Cancel on window close. Helpers/services dispose/join with their owning scene/session.
After warm-up, loading panels ease off the left/right edges over 0.7s while the loading image fades to the live scene and UI.
Input remains blocked through this reveal. A new game ends warm-up as soon as the squad's characters are drawable, skips the sky arrival and opens the
character editor first (`GameHost.Editor`); once it closes, the loading screen returns and the world warms up as above. Sky loads dismiss only panels that were shown, keeping the sky flight visible.

Meitou UI mode presents title controls over a live view of the real world; Faithful retains
the original title image and loading screen. Advanced settings persist two independent scene-transition
toggles in `meitou.user.json`, both on by default: `mainMenuTransitions` covers entering a new game/save
from the main menu and returning to it; `inGameTransitions` covers save loads/new games during play.
Each has a persisted speed slider (`mainMenuTransitionSpeed` / `inGameTransitionSpeed`, 0.25–4x, default 1.5x).
Disabling either uses the regular loading screen and warm-up. Enabled scene loads in Meitou use `SkyArrival`: 3.4s ascent
about a fixed eye (lighting takes the shorter way to midnight by halfway up, so the dawn title rewinds), a midnight
sky hold, then 2.4s descent at the destination, both durations divided by the selected speed.
Ascent ramps speed over its first/last40%; descent ramps up over30% and brakes over15%, keeping
its cruising speed later into the reveal. Integrated smoothstep velocity brings
angular velocity and acceleration to zero at both ends of each flight. Save reads and
new-game system preparation overlap the ascent; world replacement waits until terrain is outside
the view. Escape/Space skips the flight, never pending load work. Progress appears after 2s.
Camera/hour overrides belong to presentation; simulation does not advance during the flight,
and weather scheduling uses the actual game hour. Sky-only frames avoid world callbacks while
loading; warm-up renders the destination before descent. Errors drop the flight and restore UI.
`TitleComposition` shows the real world at a fixed spot near Okran's Shield (Okran's Pride),
given as a viewer camera code (`TitleComposition.Spot`, 50° lens), just after sunrise (`Sunrise + 0.75` h,
the sun peeking over the hill right of the valley; clear weather, cloud coverage 0.38). The host's renderers stream round that camera as
round any other; booting to the title starts the world there. Everything below is presentation only, set
while the title draws or shows and cleared when it closes; simulation and saves never see it:
- `SkyClock.SunTurn` turns the sun path about the vertical (height unchanged) to ~26° right of the heading;
  stars use the unturned path so their orientation stays continuous into the loading sky.
- The sky renderer's `PlanetFill` (0.8; 0 in play) lights the moons from the viewer's side so their whole face reads,
  and `PlanetTurn` (identity in play) moves them ~1.6° up and ~2.4° left in the view.
  Moon and character fill fade smoothly with sun height from the title's morning level to zero at the horizon.
- The clouds drift right to left across the view at 125 units/s (`CloudWind`), faster than the game's wind.
- Menu dust uses a separate particle renderer with the installed `kenshi_weather_dust1` system at 0.3 density,
  warm tint and 24s wind gusts; it runs while paused and fades out as the camera tilts to the stars.
- The shafts' air layer is raised to 2x and their strength to 1, so the hill the sun peeks over casts
  visible rays through the haze; `FogVolumes.HidePlaced` leaves out the placed `fogfeatures.dat` volumes
  (one stood as a dark dome where the sun is). `PostOptions.Balance` (1.1, 0.98, 0.88; 1 in play), a white balance
  before the tone map, keeps the orange of a lower sun.
- `WorldObjectRenderer.Staged` adds the foreground: a cliff top for the wanderer, the flat-topped hoodoo `FLatTop_Pillar01`
  (drawn through its FOLIAGE_MESH record `FlatTop_Hoodoo01`, terrain texturing), its top ~45 units ahead at the bottom of the
  frame and the block moved 125 units left, so its sheer right side drops to the valley just right of him; on that top, a
  little scree and a stone scaled to pebbles, desert shrubs on its left and a tiny one at its right edge (FOLIAGE_MESH records
  drawn as staged meshes), a few more on the far hill; and the
  buildable `Watchtower` building on the valley floor ~2600 units ahead at the far right, yaw 145° from the view, laid out as a placed
  building (setting or clearing it lays their zones out again). The object streamer reads a staged FOLIAGE_MESH's TERRAIN
  texturing from `material type` (a map feature's from `texture mode`); without it the mesh loses its level indices and
  draws white (**Observed** on this title).
- `FoliageRenderer.Shift` moves the ghost-town ruin (FOLIAGE_MESH houses and scrap piles) ~400 units left,
  applied as foliage zones are accepted.
- One wanderer (sandbox character in drifter's gear, with a stronger build: `CharacterAppearance.Shape`, null in play, sets 0.6
  muscle and broader shoulders, frame, chest, arms and neck) stands on the plateau's top where the ray through his screen point
  (x 0.337, y 0.93) meets it, close to the eye, lit by a warm fill (`CharacterRenderer.Fill`, 0 in play: added ambient,
  three times as strong at the silhouette) so his back, turned to the sun, is not a flat silhouette, ~300 px tall at 900 p, his back to the camera looking
  10° left of the heading, in a frozen `idle_stand_confident` pose. The camera's `MaxNear` is lowered to 20 for
  the title (the near plane is a quarter of the eye's ~350-unit clearance, ~87, which clipped him).
- Three Paladin–Dust Bandit pairs fight on the foreground road in `TitleBattle`'s separate world, using the regular combat and
  animation systems with zero damage and no medical tick. Real time advances them while gameplay is paused; their pair centres
  stay at the authored road points despite animation root motion. Bodies and pair spacing render at 2.5× scale for visibility,
  grounded on the road; combat keeps its normal dimensions. Their poses update every frame, exempt from distance throttling.
  They start three seconds into combat.
The title menu is the [Meitou UI kit](meitou-ui.md)'s main menu (`MeitouTitleMenu`): its stack, save card and plate buttons at the kit's
1600×900 sizes scaled with the height, over the kit's vignette. The card (paper, edge, rounded corners, drop shadow), the plates (Button1Skin
9-slice at 70%, pushed skin at 85% under the mouse) and the chips are baked on the CPU from crops of `Kenshi_UI.png` and uploaded as
the atlas is (UNORM, so they draw in its colours); icons are atlas rectangles. The card shows the newest save's squad, day, place,
money and members (play time is not read from saves yet) and is Enter's default. The original buttons stay as invisible hit targets at the
same bounds, so input is the faithful screen's. Lettering is Exo 2 from the painter's 16 px atlas (ASCII and Latin-1 up to U+00B7),
or a 48 px bake past 1.25x, so it stays sharp.
The Meitou wordmark, top right, is `assets/branding/kenshi-logo.png` (Lo-Fi's logo, embedded; see THIRD_PARTY_NOTICES),
because the game ships none as a file ([ui-screens](game/ui-screens.md#title-artwork-and-location)).
No title actor enters gameplay or save data; the normal camera lens and character source
are restored when leaving the title.
Title character preparation starts ahead of ordinary streaming during boot; the complete scene uses the readiness contract above.
`--title-preview --screenshot <path>` captures it for visual QA; `--ticks` advances the fight. Save arrival still uses the
loader's leader-focused camera contract; saved camera pose restoration remains unimplemented.

## Multiplayer

`GameHost.Network.cs` holds the role (`NetRole.None` in single-player: no net object, every pump a null check), the `NetHost` or
`NetClient`, the `NetSessionView` the interface reads (`player.Net`) and the request dispatch (`player.MultiplayerRequested`:
host, join, cancel, stop hosting, leave; `LeaveGame(Quit)` ends the loop). Protocol and loops: [net](net.md).

- **Frame order** (`GameHost.Interactive.cs`): `DoEvents → CoveredFrame → … → NetBeginFrame(frameDt) → if (!Covered)
  session.Advance → DuelHoldClock → WorldNews → NetEndFrame → HandleSaves → HandleNewGame → HandleMainMenu → StepSceneLoad`.
  `NetBeginFrame` = Steam's callbacks, then `netHost.BeginFrame()` / `netClient.BeginFrame(dt)` (transport events, also while
  covered; the client's presentation and clock); `NetEndFrame` = `netHost.EndFrame(Covered)` / `netClient.EndFrame()`, the client's
  frame step and the Steam step (`GameHost.Steam.cs`: rich presence, a Steam "Join game"), before a save or load can swap the world. Only `session.Advance` ticks the world.
- **Identity**: the config's `PlayerId` (made and saved on first use), `SanitizeName(--name ?? PlayerName ?? user name)`, build
  `Tag (Commit)`, and the data set hashed once on a worker (`DataSetIdentity.Compute(LoadOrder.FromInstall)`), started by the
  first multiplayer request.
- **News** (`GameHost.News.cs`): a world message for player 0 or everyone is a notice and a log line; while hosting, one for
  another player or everyone is also sent as `MessageNet`. A knock-out by player 0's character is a notice, by another
  player's a `KnockoutNet` to its owner (`KnockoutText` builds both).
- **Debug**: the F11 statistics get a `net` line (`NetHost.StatsLine()` or the client's link figures); the window title ends
  with `| host, N players` or `| client of address`. On a client the settings' simulation rule rows read and lock to the host's
  (`SettingsMenu.HostRules`).

- **Command line** (`GameOptions`): `--host [port]` (0 or none: the settings' port) hosts once a game runs, at once with
  `--new-game`/`--load`; `--join <addr[:port]>` (or `steam:<steamid64>`, also `+meitou_join <id>`) joins from the title after the boot's loading screen lifts; `--name` (this run
  only), `--password`, `--config <file>` (another settings file, so a second instance has its own player id). Refused (exit 2,
  `NetProblem`): `--host` with `--join`; `--join` with `--new-game`, `--load`, `--list-starts`; either with `--sandbox`, `--duel`,
  `--no-population`, `--title-preview`, `--ui-showcase`, `--move-to` or `--screenshot`; a bad address or port; a name that is
  blank, over 24 characters or has control characters; a password over 32; `--name`/`--password` alone. **Verified**
  (`MultiplayerOptionsTests`). A second game in the same folder logs to `log.2.txt` (up to `log.4.txt`).

### Host

`GameHost.Hosting.cs`. `HostGame(settings, start, folder)` (Multiplayer window, Esc menu) remembers the settings and, for a new
game or a save, runs the usual new-game or load flow; the hosting starts on the first frame the game is ready: uncovered, no
scene load, a player faction and population, the new game's editor closed, not the duel or sandbox, a local session. A failed
scene load forgets the request; asking to host the running game with none running is refused with a notice.
- **Start**: `NetHost(session, transport, options, NetIdentity(), link, catalog)` with the window's port, players,
  password, clock policy (`ClientsControlTime` → `AnyPlayer`, else `HostOnly`) and the host's name; a port that cannot be bound
  logs and shows `Cannot host on port {port}: …` and the game stays single-player. Then the role, `player.Net`, the modal pause
  (`EnterNet`), the settings saved to the config, `Hosting on UDP port {port}: up to {n} more players`. The transport is
  `LiteNetTransport`, or with Steam running `CompositeServerTransport(LiteNetTransport, SteamNetTransport)`, and a second notice
  says `Steam: friends can join you from their Multiplayer window` or why Steam is off ([net](net.md#steam-transport)).
- **`IHostGame`**: `Gate` = cheats from the window and `Placeable` = `PlayerInterface.PlanPlaceable` (the placement rules against
  the sites, no characters or queued ghosts; walls and a host without build data pass; an unknown record fails); `Rules` = this
  game's simulation settings (`CurrentRules`); `Weather()` = revision, forced weather, day, hour and every region's state,
  rebuilt when `WorldWeather.Revision` moves (a save's weather restored, a weather chosen or forced) or once a second.
- **Looks**: every run's appearance source is a `RecordingAppearances` round `GeneratedAppearances`, so characters made before
  hosting have recipes for the clients.
- **Saves**: a load while hosting passes `SaveLoadOptions.LocalPlayer = PlayerId` (the save's players keep numbers and squads,
  this game is 0); a single-player load passes `Guid.Empty` (every squad to 0). A finished save sends `The host saved the game.`
  to every client. Clients' save and load requests are refused (`Only the host can save or load.`).
- **Stop**: `StopHosting` (Esc menu), the main menu and quitting drop every client (`HostClosed`) and dispose the host; the game
  goes on single-player (`ClockAuthority` null, the extra sessions stay).


### Client

`GameHost.Client.cs`. `JoinGame` → `NetClient(LiteNetTransport, NetIdentity(), catalog)` (a `steam:<id>` host:
`SteamNetTransport`; refused with a notice when Steam is off) and `Connect`; connecting and the
handshake run behind the Multiplayer window (no scene load, so closing the window still cancels), with 60 s for the host's first
`WorldReset` (`The host did not send its world.`). Engine choice against the contract's "connect"/"host" scene-load stages.
- **Each `WorldReset`** (the join, then every host world swap) loads under the loading screen: "world" (`ReplaceWorld([],
  MakeReplica(seed), Replica)`; population, aggro, player squad, loaded save and pending creation dropped; navmesh zones unloaded;
  clock from the anchor; camera on `Focus`; the reset's weather; blood emptied; `Attach`, `Ready`), "people" (the first host
  snapshot on show, at most 60 s), "finishing" (`SettleNewWorld`, which sends the focus and runs no tick on a replica,
  `FollowSelected`; the first time `Joined {host}'s game` and the address into the recent games). The warm-up's population
  readiness on a replica is `HasPresented`.
- **Every frame** (`ClientFrameCore`): an ending reported by the client is handled; the host's relations of the player faction
  replace the client data's other factions' entries towards it when `(epoch, RelationsRevision)` moves (only when the host's
  player faction is the data's; restored on leaving); `InCreation` with the own active squad in view opens the character editor
  once per world (its edits and the finish are commands; `ResumeAfterCreation` runs no tick on a replica); `SetView(camera eye,
  target, character draw distance)`.
- **Host events**: messages and command refusals are notices (refusals as errors); knock-outs use `KnockoutText` with the
  attacker's snapshot name; weather (`Restore` of the regions at the host's day and hour, its forced weather); luckyren Mode
  follows the host's `SessionRules`.
- **Leaving** (Esc Disconnect/Exit, the main menu, a disconnect, a failed join): the client disposed (the host is told), the
  scene load, warm-up and creation state dropped, relations restored, the clock given back (speed 1), the role cleared. Quitting
  ends there. A join that failed before a replica was built leaves the ended view for the Multiplayer window to show the reason;
  otherwise the main-menu world is built under the loading screen and, unless the player left, `ShowDisconnected(reason,
  reachedPlay)`. A client cannot start a new game (`Leave the host's game first.`) or save and load.


## Rendering and upscaling

WorldFrame prepares/streams sky/shadows/reflection, scene depth slices and post. Algorithms
live in render-* references; Data holds backend-independent terrain/placement/light rules.
Own GLSL compiles through bundled shaderc, explicit native resource/state recording and ordered
secondary jobs. [Native API](renderer-native.md).

Single-sample jittered HDR reconstruction, separate near/far depth, unjittered reflection/shadows.
FSR Vulkan SDK1.1.4 runtime DLL; Streamline2.14.16 manual hook, initialize before Vulkan, request
features and proxy present. Bottom-up images require matching clip matrices/presentation.
Vendor DLLs are not committed; paths/packaging/licensing: [notices](../THIRD_PARTY_NOTICES.md).
Mip biases and temporal/GI contracts: [post](render-post.md). GI requested by default unless
--no-gi, unsupported ray queries fall back to flat ambient; gi Enhancement chooses output.

## Validation and performance

Tiering/PGO off, serverGC4 heaps, compact after loading then SustainedLowLatency; avoid streaming
allocations and measure tails. Offscreen fixed grass sway enables repeatable pictures. Parity
scripts compare10views; establish baseline noise for temporal vendors. Game --screenshot/--ticks,
--quit-after and --load exercise host wiring; [bench](bench.md) documents measurement.
**Observed** NVIDIA OpenGL/Vulkan sampling: anisotropy -0.25 bias reduced rock mean1.21→0.0009;
other vendors untested. Alpha-to-coverage differs; depth usesD32_SFLOAT. These are reference
observations, not a supported OpenGL backend.
