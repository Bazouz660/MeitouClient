# D4: Game host integration (`src/Meitou.Game`): implementation spec

Host-authoritative co-op as fixed. This module wires the host loop, the client boot, gating and events into `GameHost`/`PlayerInterface`. It aligns with **D1** (`SimCommand.Player`, `PlayerSession`, `PublishFor`, `JoinPlayer`, `PlayerMessage`, the snapshot's `Money/PlayerFaction/Relations/Talkable/Prospected/InCreation/CreationRaces`) and **D2** (`NetEvent`, `SessionClockState`, `ClientMirror`, Control bodies). Here, **S** is the session/transport module: `NetHost`/`NetClient`, LiteNetLib, the lobby roster and the presenter over `ClientMirror`.

**Invariant.** With `role == NetRole.Single`, every changed line reduces to today's code. No new world state is hashed, so the golden pins cannot move.

## 1. Roles and plumbing (WP0)

New `GameHost.Network.cs`:
```csharp
enum NetRole { Single, Host, Client }
sealed record HostOptions(int Port, int MaxPlayers, string? Password, bool ClientsControlTime, bool AllowCheats);
sealed record JoinTarget(string Address, int Port, string Name, string? Password)
{   // "host", "host:27100", "[::1]:27100"; FormatException on a bad port (1..65535) or empty host
    public static JoinTarget Parse(string text, int defaultPort, string name, string? password);
}
sealed partial class GameHost
{
    NetRole role;
    bool IsClient => role == NetRole.Client;
    bool IsHosting => role == NetRole.Host;
    Meitou.Net.NetHost? netHost;
    Meitou.Net.NetClient? netClient;
    public HostOptions? HostOnStart { get; init; }   // --host
    public JoinTarget? JoinOnStart { get; init; }    // --join
    /// <summary>The tick the shown world is at: the world's own, or a client's newest snapshot's.</summary>
    long WorldTick => IsClient ? session.CurrentSnapshot.Tick : session.World.Tick;
    void Submit(SimCommand command) { if (netClient is { } c) c.Send(command); else session.World.Commands.Enqueue(command); }
    void PumpNetwork() { PumpHost(); PumpClient(); }
    void AfterAdvance() { ForwardWorldOutputs(); FollowHost(); }
    partial void PumpHost(); partial void PumpClient(); partial void HookWorldOutputs();
    partial void ForwardWorldOutputs(); partial void FollowHost(); partial void PublishToClients();
    bool simulationDeferred;                                   // a --join boot skipped the simulation services (§3)
    Task EnsureSimulationServices() => Task.CompletedTask;     // body: WP-B
}
```
Classic `partial void` methods compile without their bodies, so WP-A, B and D build alone.

**Frame loop** (`GameHost.Interactive.cs:382-450`), new lines marked:
```
window.DoEvents(); … CoveredFrame; AdvanceSkyArrival; … DuelFrame(…);
PumpNetwork();                                  // NEW: between DoEvents and Advance, also while Covered
… panel input clear, EdgePointer …
HookWorldOutputs();                             // NEW (host): the ticks write into the tap
if (!Covered) session.Advance(frameDt);
DuelHoldClock(); advanceTotal += …;
AfterAdvance();                                 // NEW: host forwards outputs; client follows clock, speed, relations, creation
WorldNews();
PublishToClients();                             // NEW (host): this frame's snapshot before a save or load can swap the world
HandleSaves(); HandleNewGame(); HandleMainMenu(); StepSceneLoad(); …
```
The F11 stats get `net         {netHost?.StatsLine() ?? netClient?.StatsLine()}`. The window title gets `| host, N players` or `| client of addr`.

**PlayerInterface seams** (WP0; defaults keep single-player identical):
```csharp
public NetRole Role { get; set; }                       // menu gating, modal pause
public Action<SimCommand>? Submit { get; set; }         // Say: Submit?.Invoke(c) ?? session.World.Commands.Enqueue(c)
public Action<TimeRequest>? RequestTime { get; set; }   // PressButton: when set, hands Pause/1/2/5 to it
public IPlayerFacts Facts { get; set; }                 // = new LocalPlayerFacts(session)
public ConcurrentQueue<Floater>? FloaterFeed { get; set; }   // set: DrawFloaters reads it, the Floaters setter leaves World.Floaters alone
public string? PausedBy { get; set; }                   // for the UI module's paused panel
public void RefreshMenuPause() => UpdateMenuPause();
```
```csharp
/// What the interface reads of the player outside the snapshot: the world's own in a local game, the host's copy in a client.
interface IPlayerFacts
{
    int Faction { get; } long Money { get; } bool Exists { get; }
    IReadOnlyList<string> CreationRaces { get; } ulong Seed { get; } long Tick { get; }
    ProspectReport? Prospected { get; }
    IReadOnlyList<CharacterId> Selection { get; } CharacterId Inspected { get; }
    bool CanTalk(CharacterId npc, CharacterId talker);
}
```
- `LocalPlayerFacts(WorldSession s)`: `s.World.Player.{Faction, Money, Exists}`, `Player.Local.{CreationRaces, Selection, Inspected}`, `s.World.{Seed, Tick, Prospected, CanTalk}`. D1's `Local` renames land here only.
- `RemotePlayerFacts(WorldSession s)`: `CurrentSnapshot.{PlayerFaction, Money, CreationRaces, Tick, Prospected}`; `Exists = PlayerFaction >= 0`; `Seed` from the last `ClientWorldState`; Selection/Inspected from the characters' flags; `CanTalk = Talkable.Contains(npc)` (D1 computes it for the viewer's first selected character).

**WP0 also** moves `WorldNews` into new `GameHost.News.cs`; prepends `Stage("systems", EnsureSimulationServices, duringAscent: true)` in `HandleNewGame` and `BeginQuickLoad` when `simulationDeferred`; adds `("connect","the host",1.0), ("host","the host's game",2.0), ("systems","the world",1.0)` to `SceneSteps`; declares the §10 interfaces; creates the empty `docs/engine.md` "Multiplayer" subsections.

## 2. Host mode (WP-A: `GameHost.Hosting.cs`, `GameHost.Time.cs` new; edits `GameHost.Saving.cs`, `GameHost.NewGame.cs`, `PlayerInterface.Hud.cs`, `Meitou.Rendering/WorldWeather.cs`)

**Starting.** `HostOptions? hostPending` is set by one of three sources.
- (a) `--host --new-game`, or the lobby's "Host new game", which also sets `newGameRequested`.
- (b) `--host --load`, or "Host save", which also sets `loadRequestedFolder`.
- (c) The Esc menu's "Open to multiplayer" (`PlayerInterface.Multiplayer` → `HostGame`) for the current game.

`--host` alone waits at the title until a game runs.

`PumpHost`:
```csharp
if (hostPending is { } o && netHost is null && !IsClient && HostReady) { hostPending = null; StartHosting(o); }
netHost?.Poll();   // handshakes, gated commands, ClientFrames, time requests → IHostGame callbacks (§10)
```
`HostReady` is true when all of these hold:
- `!Covered && sceneLoad is null`;
- `session.World.Player.Exists && population is not null`;
- `!player.EditorOpen && pendingCreation is null && !creationWarmOwed` (the host makes their own characters first);
- `!IsDuel && !IsSandbox`.

**`StartHosting(o)`:**
1. `netHost = NetHost.Start(new NetHostOptions(o.Port, o.MaxPlayers, o.Password, config.PlayerGuid, PlayerName, DataFiles(), animationLibrary!), this)`; on `SocketException`: `Notice($"Cannot host on port {o.Port}: {e.Message}", true)`, stay Single.
2. `session.World.Player.Local.Guid/Name` (D1); `role = Host; player.Role = Host; player.RefreshMenuPause()` (the open Esc menu must stop pausing).
3. `session.TimeRequests = r => ApplyTimeRequest(0, r); player.RequestTime = session.TimeRequests; player.FloaterFeed = new(); outputs = new HostOutputs()`; reset the sent trackers, `epochSent = worldEpoch`.
4. Save the choices into `config`; notice and log `"Hosting on UDP port {Port}: up to {MaxPlayers-1} more players"`.

**`PublishToClients`** runs every frame while hosting:
```csharp
var phase = Covered ? HostPhase.Loading : HostPhase.Playing;
if (phase != phaseSent) { netHost.SetPhase(phase); phaseSent = phase; }
if (phase == HostPhase.Loading) return;
if (epochSent != worldEpoch) { epochSent = worldEpoch; ResetSent(); netHost.WorldChanged(); }   // S: renumber by GUID, PlayerConnected again, WorldStateFor + Decals per client, new replicators
if (!ReferenceEquals(session.CurrentSnapshot, snapshotSent)) { snapshotSent = session.CurrentSnapshot; netHost.Publish(); }  // S rate-limits; calls World.PublishFor(n)
SendRulesIfChanged(); SendWeatherIfChanged();
```
- **Rules:** `CurrentRules()` (§4) is compared with the last sent value by record equality. This covers every settings callback, including luckyren, which has none.
- **Weather:** `WorldWeather` gets `public int Revision { get; private set; }`, bumped in `Restore` and `Choose`. When `gpu.Weather.Revision != weatherSent`, send `WeatherOf(gpu.Weather)`. Natural schedule changes are not sent: the clients' scheduler runs the same seed (`WorldFrame.WeatherSeed = 1`) on the same clock, so its result is identical.

**Joins** (`IHostGame.PlayerConnected(n, guid, name)`, host thread, between ticks):
```csharp
world.Player.Join(n, guid, name);
var near = playerSquad is { Leader.IsNone: false } s && world.Characters.IsAlive(s.Leader) ? world.Characters.Previous[s.Leader.Slot].Position : session.Camera.Current.Target;
world.Commands.Enqueue(new JoinPlayer(null, near) { Tick = world.Tick, Player = n });   // D1: rejoin keeps their squads; else a Wanderer squad, session InCreation
player.Notice($"{name} joined the game");  netHost.Send(-1, new NoticeNet(new Notice(NoticeKind.Joined, n, 0)));
```
`WorldStateFor(n)` (§4) is called by S after `PlayerConnected` and after each world change. Its `Focus` is the player's own active squad leader, else `near`.

**Multiplayer new game.** In `HandleNewGame`'s "people" stage, while hosting, the `ReplaceWorld` setup runs `StartPlayer` and then, for each connected player n, `Player.Join(n, …)` and `built.Population.AddPlayer(world, n, chosen, leaderPos)` (D1 §6). Those clients see `InCreation` and open their editors.

**Saving and loading** (host only):
- `CaptureOptions()`: unchanged. D1 writes the roster and owners from the sessions' GUIDs, and a single-player save has no GUIDs.
- `SwapInSave` passes `SaveLoadOptions { LocalPlayer = IsHosting ? config.PlayerGuid : Guid.Empty }`. Single-player loads stay as today, so all squads go to 0.
- After a host load, `WorldChanged()` remaps the connected peers by `FindByGuid`, and their `JoinPlayer` takes the rejoin path.
- A successful save also sends `MessageNet("The host saved the game")` to everyone.
- **Engine choice:** a multiplayer save loaded in single player and then opened to multiplayer gives every squad to player 0. Returning players get new squads.

**Time** (`GameHost.Time.cs`):
```csharp
void ApplyTimeRequest(int who, TimeRequest r)   // not `player`: that is the PlayerInterface field
{
    if (who != 0 && !hostOptions.ClientsControlTime) { netHost?.Send(who, new MessageNet("Only the host can change the game speed")); return; }
    var sim = session.Simulation; var before = (sim.Speed, sim.LastNonZeroSpeed);
    switch (r) { case TimeRequest.TogglePause: sim.TogglePause(); break; case TimeRequest.Speed1: sim.SetSpeed(1); break; case TimeRequest.Speed2: sim.SetSpeed(2); break;
                 case TimeRequest.Speed3: sim.SetSpeed(5); break; case TimeRequest.Faster: sim.Faster(); break; case TimeRequest.Slower: sim.Slower(); break; }
    if ((sim.Speed, sim.LastNonZeroSpeed) == before) return;
    var kind = sim.Speed == 0 ? NoticeKind.Paused : before.Speed == 0 ? NoticeKind.Resumed : NoticeKind.Speed;
    netHost?.Send(-1, new NoticeNet(new Notice(kind, who, (int)sim.LastNonZeroSpeed)));
    if (who != 0) player.Notice(TimeText(kind, NameOf(who), sim.LastNonZeroSpeed));
    player.PausedBy = sim.Speed == 0 ? NameOf(who) : null;
}
static string TimeText(NoticeKind k, string who, double speed) => k switch
{ NoticeKind.Paused => $"{who} paused the game", NoticeKind.Resumed => $"{who} resumed the game", _ => $"{who} set the speed to x{speed:0}" };
```
- `PressButton`: `if (RequestTime is { } ask) ask(index == 0 ? TimeRequest.TogglePause : (TimeRequest)(index)); else …today`. This needs `Speed1..3` to equal 1..3 in the enum.
- S reads pause, speed and `World.GameHours` for `SessionClockState`.

**Stopping.** `StopHosting(string reason)`: `netHost.Stop(reason)`, `role = Single`, hooks nulled, and the outputs re-hooked locally (`World.Sounds = characterSounds is null ? null : soundQueue`, `BloodMarks = bloodQueue`, `Floaters = player.Floaters == Off ? null : new()`), else the tap grows unread. `HandleMainMenu` calls `StopHosting("The host ended the game")` first; `Run()`'s `finally` calls `netHost?.Stop("The host closed the game")`. `MultiplayerRequest.Kick` → `netHost.Kick`.

**Placement re-check** (`GateOptions.Placeable` for D1): false for a record `BuildData()` lacks, else `PlacementCheck.Evaluate(new PlacementQuery(data, p.Position, p.Rotation, YawOf(p.Rotation), p.Floor, BuildHost.Parts(p.Record), Queued: [], Characters: [], CurrentSnapshot.Buildings, BuildHost.Towns(), BuildHost.PlayerFactionId(), BuildHost.Placement) { WallPiece = wall, Ignore = wall ? (_, _, _) => true : null }) == PlacementFailure.None`. Characters are left out (the client's view lags); a footprint still loading passes.

**Looks.** Every interactive run wraps `new RecordingAppearances(new GeneratedAppearances(db, install.Root))` (D2), because (c) needs recipes for characters made before hosting; D2 must guarantee the wrapper never changes a look.

## 3. Client mode (WP-B: `GameHost.Client.cs` new; edits `GameHost.cs`, `GameHost.Transition.cs`, `GameHost.Editor.cs`, `GameHost.Characters.cs`, `GameHost.Building.cs`, `PlayerInterface.Prospecting.cs`; `RemotePlayerFacts`)

**Boot without simulation.** `BootSimulation` is split into `BootServices` (lines 127-134) and `BootGameData(homes)` (135-170). Run with `JoinOnStart`, it does this:

| Part | `--join` boot | Why |
|---|---|---|
| `GroundHeights` (kept in a field), scene, renderer, terrain, objects, foliage, water, sky, weather, particles, lamps, decals, debris, audio | yes | drawing; the clock's constants |
| `levels.Towns()`, `PopulationData.Create(db, towns, appearances, biomes: null, buildings: null, roads: null)` | yes | map sites and factions, relations, armour, names, editor previews, `LookCache` source |
| `CharacterRenderer.CreateRigs`, `AnimationLengths`, `AnimationLibrary` | yes | renderer, audio events, D2 `AnimationHash` |
| `NavSystem`, `NavAdapter`, `RagdollGeometry` | no | walkability is `OpenGroundWalkability` (never queried) |
| `BiomeMap`, `TownBuildings.Build` (homes), `FoliageResourceNodes` | no | population only |
| `BuildSystems`, `ZoneGate`, `StartPlayer` | no | `population = aggro = null`, systems `[]` |

- `simulationDeferred = true`.
- `EnsureSimulationServices()` runs `BootServices` and `BootGameData(homes: true)` on a worker, then sets `simWalkability = new NavAdapter(nav.Walkability)`.
- From then on, every `ReplaceWorld` passes `walkability: simWalkability`. That is the Engine overload of §10, because a session made on open ground would otherwise keep it.
- A client that joined from the title already has the services. It unloads `navZones`, and `UpdateNav` returns at once when `IsClient`.

**Join flow** (`BeginJoin(JoinTarget t)`; also run by `--join` right after `Boot`, instead of `BeginWarm`; `BootPlayer` skips `ShowTitle` then):
```csharp
StartSceneLoad(player.TitleOpen,
  Stage("connect", () => joined = ConnectClient(t), duringAscent: true),            // NetClient.Connect(...): Task<JoinResult>, 10 s timeout
  Stage("host", () => { if (!joined!.Result.Accepted) throw new SceneLoadFailed(joined.Result.Reason); return (stateArrived = new()).Task; }, duringAscent: true), // 60 s
  Stage("world", () => { EnterClientEpoch(pendingState!); return null; }),
  Stage("people", () => netClient!.View.FirstSnapshot),                               // completes when a snapshot of the epoch is assembled
  Stage("finishing", () => { SettleClientWorld(); return null; }));
```
`StepSceneLoad`'s catch, when `joining`, calls `LeaveClientMode(message, error: true)`, so a `--join` run that fails lands on the title.

**`EnterClientMode()`** (first epoch only): `role = Client`; `player.{Role = Client, Facts = remoteFacts, Submit = Submit, RequestTime = r => netClient.RequestTime(r), FloaterFeed = new()}`, `player.RefreshMenuPause()`; `session.Replica = netClient.View; session.TimeRequests = player.RequestTime`; `population = aggro = playerSquad = loadedSave = preparedBase = null; debugMenuOpen = false`; unload nav zones; copy the player faction's `popData.Relations` column aside.

**`EnterClientEpoch(ClientWorldState s)`** (every host epoch):
1. `EnterClientMode()` if needed; `session.ReplaceWorld([])`: a fresh **shell world** (no systems, never ticked) whose new identity resets every `ReferenceEquals(x, session.World)` cache (blood, sounds, doors, emitters, turrets, buildings, biome, UI sounds, prospect).
2. `worldEpoch++; hostEpoch = s.Epoch; clientState = remoteFacts.State = s; rules = s.Rules`.
3. `ApplyWeather(s.Weather)` when present; `BloodForNewWorld(null)` (the Decals bodies then `decalStore.Load`).
4. `session.Camera.Place(new(s.Focus.X, s.Focus.Z), view.Yaw, view.Pitch, view.Distance)`; the clock step of `FollowHost` with a forced `SetTime`.

**`SettleClientWorld()`:** `sentFocus = false; SendFocus(); indexedFor = null; previousById.Clear(); player.Reset(); player.CloseFrontend(); player.FollowSelected(); joining = false; config.LastJoinAddress = …; Notice($"Joined {NameOf(0)}'s game")`.

**Warm-up.** `CoveredFrame` uses `populationReady = TitleVisible || (IsClient ? netClient!.View.Ready : session.World.PreparePopulation(...))`. Everything else is unchanged: characters, terrain, objects and foliage readiness read the snapshot and the renderer.

**`FollowHost()`** runs every frame on a client, after `Advance`:
- **Clock.** `SessionClockState c = View.Clock` gives `h = c.GameHours`. Compute `d = (h - session.Clock.TotalHours) * GameClock.SecondsPerGameHour`. If `0 <= d < 10`, call `session.Clock.Advance(d)`; this keeps `HoursSinceStart`, the heat haze, continuous. Otherwise call `SetTime(floor(h/24), h mod 24)`.
- **Speed.** `sim.SetSpeed(SimulationClock.Speeds[c.SpeedIndex]); if (c.Paused) sim.SetSpeed(0)`. This mirrors the host for the HUD, the paused panel, weather, audio and the move marker. The client never ticks: with `Replica` set, `AdvanceSimulation` runs no world tick.
- **Relations.** When `(worldEpoch, snapshot.RelationsRevision)` changes, apply `popData.Relations.Set(f, P, row[f])` for every f, with `P = snapshot.PlayerFaction` (equal to the client data's `Relations.PlayerFaction`, Nameless; else log and skip). The player's row is read from the other faction's table (`FactionData.cs:167-169`), so `Get(P, f)` and `Get(f, P)` then equal the host's, and the colours, map standings and turret targets follow. NPC-to-NPC relations stay the data's (engine choice: only turret aim sounds read them).
- **Creation.** If `snapshot.InCreation`, no editor is open, `pendingCreation is null`, `creationAsked != worldEpoch` and the own active squad (`PlayerSquads`/`ActiveSquad`) is present, then `pendingCreation = [.. squad.Members]; creationAsked = worldEpoch`. `OpenPendingEditor` then opens it, and the editor sends `EditCharacter`/`FinishCreation` through `Submit`. `ResumeAfterCreation` skips `RunTick` when `IsClient`.
- **View.** `netClient.SetView(new ViewState(camera.Eye, camera.Target, gpu.Characters?.DrawDistance ?? 6000))`.

**Reloads.** `HostPhase.Loading` while playing starts `StartSceneLoad(false, Stage("host", stateArrived), "world", "people", "finishing")`.
- A `WorldState` with a new `Epoch` completes `stateArrived`, or starts the same load without "host".
- `HostPhase.Playing` with no new epoch completes it with `clientState`. The "world" stage then skips `EnterClientEpoch` when `Epoch == hostEpoch`.

**`LeaveClientMode(string reason, bool error)`** (disconnect, kick, Esc Exit → main menu, failed join): clear `sceneLoad`, `skyArrival`, `presentationHour`, `warming`, `pendingCreation`, `creationWarmOwed`, `joining`; `netClient?.Leave(); netClient = null; session.Replica = null; session.TimeRequests = null`. Then `StartSceneLoad(true, Stage("build", …))`:
1. Restore the relations column; `role = Single`; the `player` seams back to their defaults.
2. Not `simulationDeferred`: the `HandleMainMenu` build (`BuildSystems`, `ReplaceWorld(…, walkability: simWalkability)`, population, `ZoneGate`). Deferred: `ReplaceWorld([])` (the title needs no population; New game or Load runs "systems" first).
3. `worldEpoch++; SettleNewWorld(); player.ShowMainMenu(); AudioMainMenu(); player.Notice(reason, error)`.

## 4. Replicated world state (records: WP-E, `src/Meitou.Net/World/`)

What the client cannot derive and the snapshot does not carry:
- money, faction, relations row, talkable, prospect and creation are D1 snapshot fields;
- clock, pause and speed are D2 `SessionClockState`.

```csharp
public sealed record ClientWorldState   // Control body, reliable; at join and after each host world change
{
    public required int Epoch { get; init; }          // the host's worldEpoch
    public required int Player { get; init; }         // the receiver's number (may change after a host load)
    public required ulong Seed { get; init; }         // World.Seed: MapItems.Reveals
    public required Vector3 Focus { get; init; }      // where the camera goes
    public required SessionRules Rules { get; init; }
    public WeatherSync? Weather { get; init; }        // null: the host has no scheduler (forced by --weather → Forced set instead)
}
public sealed record SessionRules(int AttackSlots, bool LateDefences, bool DoorsCloseBehind, bool KeepRoamers, bool OffscreenFights, bool OffscreenHealing,
    bool CampaignAncestorsOnce, bool KeepWild, bool LuckyrenMode, bool ClientsControlTime, bool AllowCheats, int MaxPlayers);   // also its own body on change
public sealed record WeatherSync(int Revision, string? Forced, int Day, double Hour, IReadOnlyList<WeatherRegionSnapshot> Regions);   // own body on change
public sealed record DecalsSync(int Epoch, int Part, int Parts, IReadOnlyList<TerrainDecal> Decals);   // join/epoch only, ≤ 4096 decals a part, ≤ 20 000 in all (newest kept)
public enum HostPhase : byte { Playing, Loading }
public static class WorldStateCodec   // D2 NetWriter/NetReader; every float raw f32/f64, limits checked, NetFormatException on bad input
{ public static void Write(NetWriter w, ClientWorldState s); public static ClientWorldState ReadState(ref NetReader r); /* same pair for each record above */ }
```
- **Host builders** (WP-A):
  - `CurrentRules()` comes from `config` and `hostOptions`.
  - `WeatherOf(w)` is `new(w.Revision, w.World.ForcedWeather?.StringId, (int)session.Clock.Day, session.Clock.HourOfDay, w.World.Snapshot())`.
  - `Decals()` is `decalStore?.All`.
- **Client apply** (WP-B):
  - `ApplyWeather` releases a forced weather first or forces `Forced` (`ForceWeather(string)`), then calls `gpu.Weather.Restore(Regions, Day, Hour)`.
  - Rules are stored in `rules` (settings display, luckyren bursts).
- **Derived on the client, not sent (Observed from code):**
  - *Town buildings*: level data plus the zone state's `destroyed` (`TownBuildings`, `WorldObjectLayout`); no world-state town variant is implemented, so identical data gives identical towns (a future host-side variant choice joins `ClientWorldState`).
  - *Player buildings, scaffolds, their lamps; doors; ground items; farms*: snapshot `Buildings`, `Machines` (power, `Farm`), `Doors`, `GroundItems`. *World lamps*: level data and the hour.
  - *Interior and roof cut*: `IsPlayer` positions (every human's, shared faction; engine choice). *Debris, gibs, blood after join*: rebuilt from blood events (local randomness).

## 5. Client gating (WP-C: `PlayerInterface.Frontend.cs`, `.Faithful.cs`, `.cs`, `.Ui.cs`, `.Editor.cs`, `.Doors.cs`, `.BuildingSelect.cs`, `SettingsMenu.cs`, `SettingsMenu.Drawing.cs`, `GameHost.DebugMenu.cs`)

- **Modal pause:** `UpdateMenuPause() => session.Simulation.RequestedPause = Role == NetRole.Single && (settingsOpen || faithfulScreens.HasModal)` (host and client).
- **Esc menu:** a client has New game, Save and Load disabled; Exit asks "Leave the game and return to the main menu, or quit?" (MAIN MENU raises `Multiplayer(Leave)`, QUIT leaves then exits). The host's Exit text adds "(ends the game for everyone)".
- **Save keys:** F5/F9 on a client: `Notice("Only the host can save or load", true)` (WP-A, first line of `QuickSaveNow`/`BeginQuickLoad`); `preparedBase` is already off (`population` null).
- **Time:** keys through `session.TimeRequests`, HUD through `RequestTime`; "Time of day" is already hidden in `SettingsMenu`; host clock edits reach clients through `GameHours`.
- **Settings:** `Entry` gets `bool Locked`; `SettingsMenu` gets `public Func<SessionRules?>? HostRules { get; init; }` (non-null on a client). Attackers per target, Late dodges and blocks, Squads keep their people, Fights far away, Healing far away, Wild squads stay, Doors close behind, luckyren Mode and Raids as written read `HostRules()?.X ?? config.X` and are `Locked` on a client: drawn dimmed with " Set by the host." appended to the hint, no `Hit`, skipped by `Reset()`.
- **Debug menu:** F10 opens only when `!IsClient || rules.AllowCheats` (else `Notice("The host has not allowed cheats")`); `DebugApply` reads `Facts.Selection/Inspected/Money` and sends through `Submit`; D1's gate admits Debug* only with `GateOptions.Cheats = hostOptions.AllowCheats`.
- **Command line:** duel, sandbox, crowd, screenshot, title preview and showcase are refused (§8).
- **Steering:** `PlayerInterface.cs:117,215-218` use `Facts.Tick`.

## 6. Event plumbing (WP-D: `GameHost.NetEvents.cs` new; edits `GameHost.News.cs`, `GameHost.Blood.cs`, `GameHost.Debris.cs`, `GameHost.AudioCharacters.cs`, `PlayerInterface.Floaters.cs`)

**Host tap.** The tap feeds the clients first, then the host's own consumers:
```csharp
sealed class HostOutputs { public readonly ConcurrentQueue<SoundEvent> Sounds = new(); public readonly ConcurrentQueue<BloodMark> Blood = new(); public readonly ConcurrentQueue<Floater> Floaters = new(); }
partial void HookWorldOutputs() { if (outputs is { } o) { var w = session.World; (w.Sounds, w.BloodMarks, w.Floaters) = (o.Sounds, o.Blood, o.Floaters); } }
partial void ForwardWorldOutputs()   // host; after Advance no tick runs, so draining is safe
{
    if (outputs is not { } o) return;
    spatial.Clear();
    while (o.Sounds.TryDequeue(out var s)) { spatial.Add(new SoundNet(s)); if (characterSounds is not null) soundQueue.Enqueue(s); }
    while (o.Blood.TryDequeue(out var m)) { spatial.Add(new BloodNet(m)); if (BloodWanted) bloodQueue.Enqueue(m); }
    while (o.Floaters.TryDequeue(out var f)) { spatial.Add(new FloaterNet(f)); if (player.Floaters != FloaterMode.Off) player.FloaterFeed!.Enqueue(f); }
    if (spatial.Count > 0) netHost!.Events(spatial);   // S filters per client (D2 EventFilter)
}
bool BloodWanted => decalStore is not null || Luckyren || pieces.Count > 0;
bool Luckyren => IsClient ? rules?.LuckyrenMode == true : config.LuckyrenMode;
```
- Because `HookWorldOutputs` is the last thing before `Advance`, the later per-frame reassignments (`UpdateBlood`, the audio's new-world hook, the floater setter) never steal events. A host with `--no-audio`, Blood off or floaters Off still feeds clients.
- **Known gap:** the out-of-band settle tick (`SettleNewWorld`) writes into the local queues, so its outputs stay on the host.

**News** (`GameHost.News.cs`, host):
- `foreach (var m in world.TakeMessages())`: `if (m.For(0)) { log; player.Notice(m.Text); }`, and when hosting send `MessageNet(m.Text)` to `m.Player` (−1 means everyone, excluding 0).
- Knockouts: `int owner = netHost is null ? 0 : world.OwnerOf(k.Attacker)`.
  - Owner 0 keeps today's `IsPlayer` notice.
  - Owner ≥ 1 is sent `KnockoutNet(k)`.
  - A shared `static string KnockoutText(string name, StealthKoEntry k)` builds both.
- The population and aggro logs stay host console only.

**Client sink** (`IClientSink.Event(NetEvent e)`, main thread, inside `netClient.Poll()`):

| Event | Action |
|---|---|
| `SoundNet` | `if (characterSounds is not null) soundQueue.Enqueue(s)`; `UpdateCharacterAudio` plays it as on the host |
| `BloodNet` | `bloodQueue.Enqueue(m)`. `UpdateBlood` on a client: skip `world.Luckyren = …`; early-return only when `!BloodWanted && bloodQueue.IsEmpty` (then clear); aging uses `WorldTick` deltas |
| `FloaterNet` | `if (player.Floaters != Off) player.FloaterFeed!.Enqueue(f)` |
| `MessageNet` | `Console.WriteLine($"relations {t}")`; `player.Notice(t)` |
| `KnockoutNet` | `player.Notice(KnockoutText(snapshot name of k.Attacker ?? "?", k), !k.Success)` |
| `NoticeNet` | Paused, Resumed, Speed: `player.Notice(TimeText(...))` unless `Player == netClient.Player`, and set `PausedBy`. Joined, Left, Kicked: "{name} joined / left / was removed" |
| `ChatNet` | UI module |
| `ProspectNet` | unused: `PollProspect` reads `Facts.Prospected`, the snapshot's per-viewer value. Its world-identity baseline resets with each shell |

**Dialogue** requests and speech bubbles are snapshot fields (per viewer, D1), and `PlayerInterface.Dialogue` is unchanged.

**Debris** uses `Burst(in BloodMark m, long now)` and `UpdateDebris(long now, float dt)`, with `now = WorldTick` (in single player the same as `world.Tick`).

## 7. Every direct world read in Meitou.Game, in client mode

| Site (Meitou.Game) | Read | Client |
|---|---|---|
| `GameHost.cs:115-179` BootSimulation | nav, ragdoll, homes, systems | data only (§3) |
| `GameHost.cs:215,221-223` hostility, map standing | `Player.Faction`, `popData.Relations` | `Facts.Faction`; applied row |
| `GameHost.cs:231` map items | `World.Seed` | `Facts.Seed` |
| `GameHost.cs:252-258`, `NewGame.cs:36-71`, `Saving.cs:44-269` | start, new game, capture, load | refused or gated; host only |
| `GameHost.cs:308-327`, `Building.cs:124-130`, `Interiors.cs:121` | nav | off; `SurfaceMoveHit` gives null (engine choice) |
| `GameHost.cs:346-358`, `Audio*.cs` speed and pause | `Clock`, `Simulation` | followed clock and mirrored speed |
| `Interactive.cs:97,113-121` | clock slider, rule callbacks | host only; locked |
| `Interactive.cs:422,565-584` | Advance, tick stats | `Replica.Advance`; net line |
| `News.cs` | messages, knockouts, logs | shell empty; sink events |
| `NewGame.cs:16-33` main menu | World | `LeaveClientMode` |
| `Transition.cs:112,180`, `Editor.cs:47` | PreparePopulation, RunTick | `View.Ready`; no tick |
| `Editor.cs:33`, `Characters.cs:32`, `PlayerInterface.cs:59`, `DebugMenu.cs:49-85` | `Commands.Enqueue` | `Submit` |
| `Blood.cs`, `Debris.cs`, `AudioCharacters.cs:64-71` | output queues, `Tick`, `Luckyren` | events, `WorldTick`, rules |
| `AudioTurrets.cs:35`, `AudioWorld.cs:61,83`, `Building.cs:30`, `PlayerInterface.Biome.cs:36`, `.Sounds.cs:91,98` | `ReferenceEquals(…, World)` | one new shell per epoch |
| `Building.cs:184-211`, `Title.cs:39`, `Editor.cs:20`, `AudioTurrets.cs:61` | `popData` | client data (row applied) |
| `DebugMenu.cs:49-52,117,133` | Selection, Inspected, Money | Facts; cheats gate |
| `PlayerInterface.cs:117,215-218`, `.Editor.cs:302` | `World.Tick` | `Facts.Tick`; batch stamp restamped |
| `PlayerInterface.cs:133` | `CanTalk` | `Talkable` |
| `.BuildingSelect.cs:48`, `.Doors.cs:38`, `.Ui.cs:106`, `.Editor.cs:276,288`, `.Frontend.cs:86-148` | Faction, Money, CreationRaces, Exists | Facts, plus role gating |
| `.Floaters.cs:29,37`, `.Prospecting.cs:47-55` | `World.Floaters`, `Prospected` | `FloaterFeed`, `Facts.Prospected` |
| `.Hud.cs:64-65`, `.Faithful.cs:17` | speed buttons, modal pause | `RequestTime`; no pause |

The ~110 `Tick = session.World.Tick` stamps stay: D1's gate restamps every remote command.

## 8. Command line and config (WP-F: `GameOptions.cs`, `Program.cs`, `UserConfig.cs`, `GameLog.cs`)

`GameOptions` gains `int? Host` (0 means `config.HostPort`), `string? Join, Name, Password, ConfigFile`, parsed before `default:`.
- `--host [port]`: a following all-digit argument is the port.
- `--join <addr[:port]>`, `--name <n>`, `--password <p>`, `--config <file>`.
- Usage lines:
  ```
  --host [port]              share the game: hosts once a game runs (with --new-game / --load at once); default port from meitou.user.json (27100)
  --join <addr[:port]>       join a host's game instead of starting one
  --name <n>, --password <p> your player name (default from meitou.user.json) and the host's password
  --config <file>            settings file instead of meitou.user.json (a second instance on one machine)
  ```

**`Program` checks** (after `Split`, exit code 2): `--host` with `--join`; `--join` with `--new-game`, `--load`, `--sandbox`, `--duel`, `--no-population`, `--title-preview`, `--ui-showcase`, `--list-starts`, `--move-to`, `--screenshot` or `--crowd` ("--join plays the host's game: drop …"); `--host` with the same list minus `--new-game`, `--load`, `--list-starts`; `--name` 1-24 characters without control characters.

**`Program` setup:** `toTitle` adds `&& game.Join is null` (a client loads round The Hub; the camera moves on join); `var config = game.ConfigFile is { } f ? UserConfig.LoadOrCreate(f) : UserConfig.Load(); config.EnsureIdentity();`; `HostOnStart = game.Host is { } p ? new(p > 0 ? p : config.HostPort, config.MaxPlayers, game.Password, config.ClientsControlTime, config.AllowClientCheats) : null`; `JoinOnStart = game.Join is { } j ? JoinTarget.Parse(j, config.HostPort, game.Name ?? config.PlayerName!, game.Password) : null` (`--name` is not saved).

**`UserConfig`** (no version bump; launcher schema unchanged):
```csharp
/// <summary>Your name in multiplayer (null: the account name, set on first run).</summary> public string? PlayerName { get; set; }
/// <summary>Your identity to hosts: keeps your player number and squads across sessions and saves (made on first run).</summary> public Guid PlayerGuid { get; set; }
/// <summary>The last host joined ("addr:port"), for the lobby's address box.</summary> public string? LastJoinAddress { get; set; }
/// <summary>The UDP port a hosted game listens on.</summary> public int HostPort { get; set; } = 27100;
/// <summary>Players in a hosted game, the host included (2-8).</summary> public int MaxPlayers { get; set; } = 4;
/// <summary>Whether joined players may pause and change the speed of a hosted game.</summary> public bool ClientsControlTime { get; set; } = true;
/// <summary>Whether joined players may use the F10 debug menu in a hosted game.</summary> public bool AllowClientCheats { get; set; }
public static UserConfig LoadOrCreate(string file);   // Load(file) when it exists, else defaults with Path = Path.GetFullPath(file)
public void EnsureIdentity();                         // PlayerGuid = NewGuid when Empty; PlayerName ??= Environment.UserName clamped to 24
```
The default port 27100 is S's `NetDefaults.Port` if S chooses another.

## 9. Two instances on one machine

- **Config:** `--config meitou.guest.json` gives a separate file and `PlayerGuid`; the same GUID twice is refused by S.
- **Log:** `GameLog.Open` tries `log.txt`, then `log.2.txt` … `log.4.txt` (today the second instance gets none, `GameLog.cs:29-35`).
- **Ports:** the host binds `HostPort`, the client an ephemeral port; two hosts need distinct `--host <port>`.
- **Load:** use `--windowed --size 1280x720 --fps-limit 60`, `--low-end` on small GPUs, and `--no-audio` on one. The loop does not throttle unfocused (Observed). S must tolerate ≥ 30 s without `Poll`, since host scene-load stages block the loop.
- **Example:**
  ```
  MeitouClient --host --new-game --name Host --windowed --size 1280x720
  MeitouClient --join 127.0.0.1 --name Guest --config meitou.guest.json --windowed --size 1280x720 --no-audio
  ```

## 10. Cross-module interfaces

**Exposed by D4.** These are declared in `src/Meitou.Net/Session/` (WP0) so that S compiles against them, and `GameHost` implements them:
```csharp
public interface IHostGame       // S calls these on the host's frame thread, inside NetHost.Poll/WorldChanged, between ticks
{
    bool Ready { get; }                                   // false: hold joins, the phase is Loading
    World World { get; }
    GateOptions Gate { get; }                             // Cheats, Placeable (§2)
    void PlayerConnected(int player, Guid guid, string name);   // after S picked the number (FindByGuid ?? NextNumber); Join + JoinPlayer + notice
    void PlayerLeft(int player, string name, string reason);    // notice; squads stay (D1 TransferSquad is a host UI action)
    ClientWorldState WorldStateFor(int player);
    IReadOnlyList<TerrainDecal> Decals();
    void TimeRequested(int player, TimeRequest request);       // → ApplyTimeRequest
}
public interface IClientSink     // NetClient.Poll calls these on the main thread
{
    void WorldState(ClientWorldState state); void Rules(SessionRules rules); void Weather(WeatherSync weather);
    void Decals(DecalsSync part); void Phase(HostPhase phase); void Event(NetEvent e); void Disconnected(string reason, bool byUs);
}
```
- **Control bodies** to reserve in S's `Control` table: `WorldState`, `Rules`, `Weather`, `Decals`, `Phase` (h→c) and `TimeRequest` (c→h, a single byte). Their codecs are `WorldStateCodec` (WP-E).
- **For the UI module** (`PlayerInterface`):
  - `event Action<MultiplayerRequest>? Multiplayer`, with `abstract record MultiplayerRequest; HostGame(HostOptions Options, string? NewGameStart, string? LoadFolder); JoinGame(JoinTarget Target); Leave(); Kick(int Player, string Reason)`;
  - `Role`, `PausedBy`, and `IPlayerFacts Facts`;
  - `GameHost.NameOf(int)`, forwarded to S.
  - The UI module owns the title MULTIPLAYER button, the lobby, the Esc "Open to multiplayer" button, chat, the player list, name tags, the own-character filter for selection (`CharacterSnapshot.Owner`) and the paused panel.

**Consumed:**
- **D1:**
  - `SimCommand.Player`, `JoinPlayer`, `PlayerState.{Local, Join, FindByGuid, NextNumber}`, `PlayerSession.{Guid, Name}`;
  - `World.{OwnerOf, TakeMessages(): IReadOnlyList<PlayerMessage>, Prospected}`;
  - `PopulationSystem.AddPlayer(World, int, NewGameStart, Vector3)`, `SquadManagement.PlayerSquads(World, int)`;
  - `GateOptions`, `SaveLoadOptions.LocalPlayer`;
  - `WorldSnapshot.{PlayerFaction, Money, Relations, RelationsRevision, Talkable, Prospected, InCreation, CreationRaces}`.
- **D2:** `NetEvent` subtypes, `Notice`/`NoticeKind`, `SessionClockState(bool Paused, byte SpeedIndex, double GameHours)`, `ViewState`, `NetWriter`/`NetReader`/`NetFormatException`, and `RecordingAppearances(IAppearanceSource)` (which must not alter looks).
- **S** (names are S's to choose; the call sites rename mechanically):
  ```csharp
  NetHost NetHost.Start(NetHostOptions o, IHostGame game);  // throws SocketException
  record NetHostOptions(int Port, int MaxPlayers, string? Password, Guid HostGuid, string HostName, IReadOnlyList<string> DataFiles, AnimationLibrary Animations);
  void Poll(); void Publish(); void Events(IReadOnlyList<NetEvent> spatial); void Send(int player, NetEvent e);   // -1: every client
  void SendControl(int player, object body); void SetPhase(HostPhase p); void WorldChanged(); void Kick(int player, string reason); void Stop(string reason);
  int ClientCount { get; } string NameOf(int player); string StatsLine();
  NetClient NetClient.Connect(NetClientOptions o, IClientSink sink);
  record NetClientOptions(string Address, int Port, string Name, Guid Guid, string? Password, IReadOnlyList<string> DataFiles, AnimationLibrary Animations, IAppearanceSource Appearances, GameDatabase Db);
  Task<JoinResult> Joined { get; } int Player { get; } IReplicaView View { get; } void Poll(); void SetView(ViewState v);
  void Send(SimCommand c); void RequestTime(TimeRequest r); void Leave(); string NameOf(int player); string StatsLine();
  readonly record struct JoinResult(bool Accepted, string Reason);
  interface IReplicaView { WorldSnapshot Current { get; } WorldSnapshot Previous { get; } float Alpha { get; } bool Ready { get; } Task FirstSnapshot { get; } SessionClockState Clock { get; } void Advance(double realSeconds); }
  ```
- **Engine** (owned by S or by the engine module; if neither adds them, WP0 adds them, about 25 lines):
  ```csharp
  public enum TimeRequest : byte { TogglePause, Speed1, Speed2, Speed3, Faster, Slower }
  public IReplicaView? Replica { get; set; }          // WorldSession: Current/Previous/SimulationAlpha read it; AdvanceSimulation then only calls Replica.Advance (no world tick, no paused commands)
  public Action<TimeRequest>? TimeRequests { get; set; }   // WorldSession.Tick: set → the six time actions go to it instead of Simulation
  public World ReplaceWorld(IEnumerable<ITickSystem> systems, Action<World>? setup = null, IWalkability? walkability = null);   // given: this and later worlds use it
  ```
  `IReplicaView` lives in `Meitou.Engine`, because `Meitou.Net` references Engine.

## 11. Work packages

| WP | Files | Depends on |
|---|---|---|
| WP0 seams | `GameHost.Network.cs` (new), `GameHost.News.cs` (moved), the loop lines in `GameHost.Interactive.cs`, `GameHost.NewGame.cs`/`GameHost.Saving.cs` "systems" stage lines, `PlayerInterface.cs` (Say, seams), `PlayerFacts.cs` (new: interface + Local), `Meitou.Net/Session/IHostGame.cs`, `IClientSink.cs`, `docs/engine.md` skeleton; Engine additions if needed | D1 WP1, S skeleton |
| WP-A host | `GameHost.Hosting.cs`, `GameHost.Time.cs` (new), `GameHost.Saving.cs`, `GameHost.NewGame.cs`, `PlayerInterface.Hud.cs`, `Meitou.Rendering/WorldWeather.cs` | WP0 |
| WP-B client boot and scene | `GameHost.Client.cs` (new, includes `RemotePlayerFacts` and sink world-state methods), `GameHost.cs`, `GameHost.Transition.cs`, `GameHost.Editor.cs`, `GameHost.Characters.cs`, `GameHost.Building.cs`, `PlayerInterface.Prospecting.cs` | WP0, WP-E records |
| WP-C gating | `PlayerInterface.Frontend.cs`, `.Faithful.cs`, `.Ui.cs`, `.Editor.cs`, `.Doors.cs`, `.BuildingSelect.cs`, `PlayerInterface.cs` (CanTalk, steering lines only; after WP0), `SettingsMenu.cs`, `SettingsMenu.Drawing.cs`, `GameHost.DebugMenu.cs` | WP0 |
| WP-D events | `GameHost.NetEvents.cs` (new), `GameHost.News.cs`, `GameHost.Blood.cs`, `GameHost.Debris.cs`, `GameHost.AudioCharacters.cs`, `PlayerInterface.Floaters.cs` | WP0, D2 events |
| WP-E world-state records and codecs | `src/Meitou.Net/World/*.cs`, `tests/Meitou.Tests/Net/WorldStateCodecTests.cs` | D2 WP1 |
| WP-F CLI and config | `GameOptions.cs`, `Program.cs`, `UserConfig.cs`, `GameLog.cs` | WP0 (`HostOptions`, `JoinTarget`) |

After WP0, A–F touch disjoint files and run in parallel (B's sink forwards `Event` to D's handler in `GameHost.NetEvents.cs`). Each appends its subsection to `docs/engine.md` "Multiplayer" (frame order and roles; host; client boot, epochs, shell worlds; gating; events; CLI and two instances), facts only.

**Tests** (approved categories only):
- `WorldStateCodecTests` (codec round-trips): every §4 record round-trips field by field; truncated or over-limit input throws.
- S's in-process memory-transport harness ("replicated view matches host"): a rules change, a `WeatherSync` after `Choose` and an epoch bump arrive in order; the client's applied relations row equals the host's `Get(P, f)`.
- Not approved, ask first: `GameOptions`/`Program` combinations and `JoinTarget.Parse` (flag clashes, IPv6), untested today.

**Gate:** the full test run, with `GoldenHashTests` unchanged.

## 12. Leave alone and edge cases

- **Leave alone:** duel, sandbox, screenshot and title-battle code; the tick stamps; `ReferenceEquals` checks; single-player `SettleNewWorld`; `StartPlayer`; save formats (D1); the renderer except `WorldWeather.Revision`.
- **Join while the host loads:** `Ready` is false; the client waits in "host" up to 60 s, then fails "The host is still loading". During the host's own editor, joins are served.
- **Host load or new game while hosting:** Loading, then a new epoch; clients reload, peers renumber by GUID, an open client editor closes in `SettleClientWorld`.
- **Disconnect mid-stage:** `LeaveClientMode`. Closing the window calls `netClient.Leave()` in `Run`'s `finally`.
- **Refusals** (version, hashes, password, full, duplicate GUID): S's reason is an error notice on the title.
- **Untrusted data:** the codec checks counts; unknown weather region ids are skipped by `WeatherWorld.Restore`; a relations row whose length differs from `Relations.Count` is ignored and logged.
