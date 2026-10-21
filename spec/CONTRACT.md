# CONTRACT: host-authoritative co-op multiplayer (cross-module, authoritative)

This file is the single cross-module contract for D1 (simulation), D2 (wire), D3 (sessions), D4 (game host) and D5 (UI).
Where it differs from a spec, this file wins. For anything it does not mention, follow the spec named in the package's
"Detail" column (§4). Engineers read this file first, then only their package's spec sections.

**User decision (2026-10-21): every test this contract marks "Ask" is APPROVED. Treat "Ask" exactly like "Approved" and add
those tests in the package that lists them.** Wave-1 "Ask" cases (S1, N1) are added by package T0 in wave 2.

Fixed decisions are not revisited: host-authoritative listen server; LiteNetLib UDP; snapshot deltas against the last acked
state on an unreliable-sequenced channel; reliable-ordered channels for commands, chat, events and views; co-op in the shared
player faction; player 0 = host, 1..N remote, stable per GUID; anyone may pause unless the host restricts it; modal windows do
not pause in multiplayer; single-player and the `GoldenHashTests` pins unchanged.

## 1. Project layout and references

**New project** `src/Meitou.Net/Meitou.Net.csproj` (created by N1, modelled on `Meitou.Simulation.csproj`):
- `ProjectReference`: Core, Data, Simulation, Engine. Never Rendering, Audio, Ui or Game.
- `<PackageReference Include="LiteNetLib" Version="2.1.4" />`. **Checked on nuget.org (2026-10-21):** 2.1.4 is the newest
  listed stable (published 2026-05-19; assets `net8.0` and `netstandard2.1`, which net10.0 consumes). The specs' 1.3.1 is
  superseded. 2.x renamed parts of the API (`LiteNetManager`/`LiteNetPeer` added; the channeled `Send` lives on the full
  `NetPeer`; `INetEventListener` merged). Only `Transport/LiteNetTransport.cs` may touch LiteNetLib types; N3 confirms every
  member it uses against 2.1.4 at build. Fallback if 2.x blocks something: 1.3.5 (last 1.x, 2025-12-16).
- `InternalsVisibleTo Meitou.Tests`.
- Also (N1): the project goes under `/src/` in `MeitouClient.slnx`; `tests/Meitou.Tests/Meitou.Tests.csproj` and
  `src/Meitou.Game/Meitou.Game.csproj` reference it; `THIRD_PARTY_NOTICES.md` gets a LiteNetLib (MIT) section with its
  licence text; `docs/net.md` is created and linked from `docs/README.md`.

**Reference graph** (arrow = references): `Core ← Data ← Simulation ← Engine ← Net ← Game`; `Tests → everything`.
Simulation and Engine never reference Net. Net never references Game, so Game-side types the net layer needs (`IHostGame`)
are declared in Net and implemented in Game.

**Folders and namespaces** (folder = namespace under `Meitou.Net`):

| Folder | Namespace | Contents |
|---|---|---|
| `Primitives/`, `Fcs/`, `Messages/` | `Meitou.Net` | NetWriter/NetReader, limits, Quant, string tables, CommonCodec, FcsRecordCodec, MessageType, NetChannel, IPacketSink, NetProtocol |
| `Transport/` | `Meitou.Net.Transport` | INetTransport family, LiteNetTransport, LoopbackNetwork, clocks |
| `Commands/` | `Meitou.Net.Commands` | tag table, CommandCodec, CommandBatch/CommandResult codecs |
| `Replication/{Snapshot,Views,Looks,Delta}/` | `Meitou.Net.Replication` | CharacterWire, view codecs, looks, interest, ClientReplicator, ClientMirror |
| `Events/` | `Meitou.Net.Events` | NetEvent types, EventBatch, EventCodec, EventFilter |
| `Session/`, `Session/Host/`, `Session/Client/` | `Meitou.Net.Session` | value records, protocol codecs, handshake, NetHost, NetClient, JitterBuffer |

Game-side new files: `src/Meitou.Game/Multiplayer/` (`NetUiContract.cs`, `PlayerColours.cs`, `JoinAddress.cs`,
`ChatCommands.cs`, `NetSessionView.cs`) plus the `GameHost.*`/`PlayerInterface.*` partials named in §4.

**Docs** (one home per fact): protocol, channels, limits, host/client loops and constants → `docs/net.md`; sessions, gate,
per-viewer publish, joining, replica worlds → `docs/simulation.md` "Multiplayer (host-authoritative co-op)"; save keys →
`docs/formats/save.md`; frame order, roles, client boot, epochs → `docs/engine.md` "Multiplayer"; windows, keys, palette →
`docs/meitou-ui.md` "Multiplayer" (not a new `docs/multiplayer.md`); Esc/title deviations → `docs/faithful-ui-parity.md`.
Parallel packages edit a shared doc only under a heading the wave-1/2 skeleton package created for them (heading, blank
line, one placeholder line), so git merges them without conflict.

## 2. Shared types and conflict resolutions

### 2a. Conflicts between specs (and their resolution)

| # | Conflict | Resolution |
|---|---|---|
| C1 | Wire primitives: D2 (`ref struct NetReader` over a span, `NetFormatException`, `WriteVarUInt` names) vs D3 (`class NetReader`, `InvalidDataException`, `U8/VarU` names, ns `Meitou.Net.Wire`) | D2's. Every reader failure is `NetFormatException`. D3's message codecs are written against D2's API. |
| C2 | Snapshot shipping: D2 self-contained parts with per-entity baselines acked per part vs D3 one frame fragmented (`SnapshotSender`, `SnapshotAssembler`, `IViewerEncoder/Decoder`, `ReplicationFrame`, `BulkView`, `ReplicationCodec`) | D2's: a lost part loses only its entities, and it is the fixed Quake3 model (ring of sent parts, deltas only against acked state). All D3 replication abstractions are deleted. D3's `JitterBuffer` consumes the `WorldSnapshot`s `ClientMirror` assembles. |
| C3 | Channel 0 delivery: D3 `Unreliable` vs D2/fixed `Sequenced` | `DeliveryMethod.Sequenced` (fixed decision). Parts stand alone, so a late part dropped by sequencing costs only its entities. |
| C4 | Channel names/roles | `NetChannel { Snapshots = 0, Control = 1, Views = 2, Events = 3 }` (D2 numbering, D3 names). |
| C5 | Two message-id tables (D2 0x01..0x40, D3 1..0x40) | One table, §2b `MessageType`. |
| C6 | Handshake: D2 Hello/Welcome with one `DataHash` vs D3 Challenge/Hello/Welcome/Ready/WorldReset with password and `DataSetIdentity` vs D4 `ClientWorldState`/`HostPhase`/`IClientSink` | D3's sequence; Hello also carries D2's `AnimationHash`; D4's Seed, rules and weather move into `WorldReset`. `ClientWorldState`, `HostPhase`, `IClientSink`, `DecalsSync` are dropped (decals: R10). |
| C7 | D2 has no world epoch | `Epoch` (u16) on SnapshotPart, ClientFrame, ViewUpdate, EventBatch, CommandBatch, Ready, WorldReset, ClockState. Messages of another epoch are dropped. |
| C8 | Clock: D2 `SessionClockState` in every part + Paused/Resumed/Speed notices; D3 `ClockState`/`ClockRequest`/`IClockAuthority`; D4 `TimeRequest`, `WorldSession.TimeRequests`, `GameHost.ApplyTimeRequest`, clock lines in `FollowHost`; D5 `RequestPause/RequestSpeed` | D3's model only. D2's part header loses GameHours and the pause/speed flags; `SessionClockState` is deleted. D4's `TimeRequest`, `TimeRequests`, `ApplyTimeRequest`, clock-follow code and D5's `RequestPause/RequestSpeed` are deleted. |
| C9 | Client world: D3 replica `World` (`MakeReplica`, `Present`, `SessionMode`) vs D4 `IReplicaView` in `WorldSession` + shell world + `IPlayerFacts` + `Submit` + `WorldTick` | D3's replica world. `World.Present` also mirrors the viewer's facts (faction, money, selection, inspected, active squad, creation, prospect) into the replica's `PlayerState`, and a replica's `CanTalk` reads `Snapshot.Talkable`; `Tick` follows the host. So `IPlayerFacts`, `LocalPlayerFacts`, `RemotePlayerFacts`, `Submit`, `WorldTick` and `IReplicaView` are dropped: UI code keeps enqueuing into `session.World.Commands`, which `NetClient` drains. |
| C10 | Host event tap: D3 mark-based `HostEventTap` + a `PlayerInterface.Floaters` change vs D4 `HostOutputs` + `FloaterFeed` in GameHost | One tap in `Meitou.Net` (N11): `BeginFrame` swaps the world's three queues for its own and remembers the consumers'; `EndFrame` drains, forwards copies to the remembered consumer queues and restores them. No Game consumer changes, `FloaterFeed` and the Floaters edit are dropped. |
| C11 | Events: D2 `EventBatch`/`NetEvent` vs D3 `EventCodec`/`TickEvents` | D2's, one batch per peer per frame. `ProspectNet` dropped (the per-viewer snapshot field carries it). `ChatNet` gains the sender's `Name`; `NoticeNet` loses the clock kinds and gains `Name` and `Detail`. |
| C12 | Messages: D3 `ServerMessage` on Control vs D2 `MessageNet` event | `MessageNet` on Events. Control keeps only protocol traffic; `CommandResult` (refusals only) stays on Control. |
| C13 | Chat: D3 Chat/ChatLine on Control vs D2 ChatRequest (ch1) / ChatNet (ch3) | D2's. |
| C14 | Commands: D2 `CommandBatch(FirstSeq)` ≤ 64, `CommandResult(Seq, Accepted, Reason)` vs D3 `Commands(epoch, firstSeq)` ≤ 256, `CommandResult(Seq, Reason)` | `CommandBatch(Epoch, FirstSeq, Commands)` ≤ 64 per message (several per frame allowed); `CommandResult(Seq, Reason)` sent only for refusals, ≤ 2/s per peer. |
| C15 | D3 assumed `CommandCodec.Write(NetWriter, SimCommand)`/`Read(NetReader)` and `CommandGate.Admit(..., out string?)` | Owners win: D2's codec signatures, D1's `GateResult Admit(World, int, SimCommand, GateOptions?)`. |
| C16 | Player roster: D3 `PlayerRegistry`, `KnownPlayer`, `World.KnownPlayers`, `World.EnsureSession`, `World.FocusOf` vs D1 `PlayerState` roster | D1's. Number for a GUID = `FindByGuid(g)?.Number ?? NextNumber()`; `HostWorldLink` (N11) holds the helpers (focus, join point, squads count). |
| C17 | Joining: D4 `JoinPlayer` on connect and `AddPlayer` for each player inside the multiplayer new-game setup; D3 only `EnsureSession` | `Player.Join` at an accepted Hello; `JoinPlayer` enqueued at every `Ready(epoch)`. The rejoin path makes it idempotent, so a new game or load while hosting needs no `AddPlayer` in its setup. |
| C18 | `JoinPlayer`/`TransferSquad` while paused: no spec handles it (the paused path ignores them) | The paused path also takes these two (they never occur in single-player): S4 adds them to the `ApplyPausedInterfaceCommands` filter and calls `PopulationSystem.ApplyJoins`; S5 implements it. |
| C19 | D4's large `IHostGame` (Ready, World, PlayerConnected, WorldStateFor, Decals, TimeRequested) vs D3's NetHost-on-WorldSession | `NetHost` owns the session logic; `IHostGame` shrinks to `Gate`, `Rules`, `Weather()`. Join/leave reach the game as `NetHost.Notice` events. |
| C20 | Default port 40120 (D3) / 27100 (D4) / 27420 (D5) | `NetProtocol.DefaultPort = 40120`; every other constant refers to it. |
| C21 | Max players 16 (D3) vs 8 (D4, D5) | 8, host included (`NetProtocol.MaxPlayers`); default 4. The palette has 8 colours. |
| C22 | `UserConfig`: three key sets | One set (§2b `UserConfig`), owned by U1. `LastJoinAddress` is replaced by `RecentGames`. |
| C23 | Second instance: D3 `--net-id new` vs D4 `--config <file>` vs D5 separate working directories | D4's `--config <file>` (its own `PlayerId`). |
| C24 | Roles: D4 private `NetRole { Single, Host, Client }` + `PlayerInterface.Role` vs D5 public `NetRole { None, Host, Client }` + `PlayerInterface.Net` | D5's. |
| C25 | Name clashes: D5 `NetEvent` vs D2 `NetEvent`; D5 `NetRules` vs D3 `NetRules`; D2 `ChatLine` vs D3 `ChatLine` | UI events become `MultiplayerEvent`; one `NetRules` (Net) used by the UI; the chat event is `ChatNet`. |
| C26 | End reasons: D5 `NetEnd` vs D3 `RejectCode` | `RejectCode` (+ `Left`, `Unreachable`) carried in `NetDisconnect(Code, Reason)`; the reason text is D3's; D5 only picks the title. `NameInUse` dropped (duplicate names get " (2)"). |
| C27 | Roster: D3 `RosterEntry` vs D5 `NetPlayer` | `RosterEntry` gains `Colour` and `Squads`; `Afk` dropped; `Away` entries stay in the roster so tags can still name a left player. |
| C28 | HUD speed buttons: D3 `RequestClock`, D4 `RequestTime`, D5 `NetTimeButton` | `PressButton` → `session.RequestClock(...)` (E1); U1 adds a pre-check that shows "Only the host can change the game speed." when the local player may not. |
| C29 | Modal pause formula (three versions) | `session.Simulation.RequestedPause = session.ModalsPause && (settingsOpen || faithfulScreens.HasModal)` (E1). |
| C30 | Weather: D3 periodic `WeatherSync(WeatherTime, Regions)` vs D4 revision-based `WeatherSync(Revision, Forced, Day, Hour, Regions)` | D4's record, sent in `WorldReset`, on `WorldWeather.Revision` change and every 60 s. |
| C31 | Host loading: D4 `HostPhase` vs D3 `ClockState.HostBusy` | `HostBusy`. A world swap is an epoch change (`WorldReset`). |
| C32 | Look recording: D2 only when hosting vs D4 every interactive run | Every interactive run (G2), so "open to multiplayer" has recipes for existing characters. |
| C33 | `CharacterSnapshot.Owner` for NPCs: D5 assumed 0, D1 says -1 | D1: owner number for `IsPlayer`, else -1. |
| C34 | Snapshot budget: D2 96 KB/s vs D3 256 KB/s | 256 KB/s per client, so typical scenes send every changed entity every send (R3). |
| C35 | Client `SendFocus`: D3 suppresses it; D1 needs the joiner's `Focus` before a squad exists | The client keeps sending `FocusCommand` (gated: stamp only). |
| C36 | D4 "boot without simulation" for `--join` | Deferred (R11). A client boots normally and discards the title world when it joins. |
| C37 | D1 says no host `RunTick` outside the session clock; D3/D4 keep `SettleNewWorld`/`ResumeAfterCreation` ticks | Allowed on the host (it is the authority); their events stay host-only (R7). Replicas never tick (S4 throws). |
| C38 | D1's `JoinTests.cs` already exists (command-queue tests) | New file `PlayerJoinTests.cs`. |
| C39 | D5: foreign ids "dropped silently"; D1: filtered, refused when nothing remains | D1. A refusal returns a `CommandResult`, shown as an error notice (≤ 2/s). |
| C40 | D3's separate `Ack` and `View` messages | Folded into D2's `ClientFrame` (acks + `ViewState`). |
| C41 | Chat length 512 B (D2) vs 200 chars (D3, D5) | 200 UTF-16 chars, host-clamped; wire limit 1024 B. |
| C42 | Pause notices: D4 notifies only for others; fixed decision says everyone sees "<name> paused the game" | Every client and the host get the line, "You …" for one's own change (D5's texts). |
| C43 | File ownership overlaps (World.cs/World.Interface.cs/World.Loading.cs in D1 WP4 and D3 WP5; WorldSession.cs in D3 and D4; UserConfig.cs in D3, D4, D5; PlayerInterface.Hud/Faithful in D3, D4, D5; GameHost.Interactive.cs in D1, D3, D4, D5) | Assigned once per wave in §4. |

### 2b. Shared types (exact C#; owner package in brackets)

**Simulation** (`Meitou.Simulation`). Bodies and the full `PlayerSession` member list: D1 §1–§7.

```csharp
// [S1] Commands.cs
public abstract record SimCommand { public required long Tick { get; init; } public int Player { get; init; } }
public sealed record JoinPlayer(string? StartId, Vector3 Near) : SimCommand;     // host only; handled by S5
// [S1] SquadManagement.cs
public sealed record TransferSquad(int Squad, int Owner) : SquadCommand;         // host only; handled by S5
// [S1] PlayerSession.cs
public sealed class PlayerSession
{
    public int Number { get; }  public Guid Guid { get; set; }  public string Name { get; set; } = "";
    public List<CharacterId> Selection { get; }  public int Squad { get; set; } = -1;
    public bool InCreation { get; set; }  public IReadOnlyList<string> CreationRaces { get; set; } = [];
    public CharacterId Inspected { get; set; } = CharacterId.None;  public string? InspectedBuilding { get; set; }
    public string? OpenContainer { get; set; }  public CharacterId ContainerOpener { get; set; } = CharacterId.None;
    public string? OpeningContainer { get; set; }  public CharacterId ContainerWalker { get; set; } = CharacterId.None;
    public string? DoorTarget { get; set; }  public CharacterId DoorWalker { get; set; } = CharacterId.None;
    public Vector3? Focus { get; set; }
    public Production.ProspectReport? Prospected { get; internal set; }
    public EditGrant? Grant { get; internal set; }
}
public sealed record EditGrant(Editing.EditKind Kind, CharacterId Character, CharacterId Surgeon);
public interface INpcHolder { int HolderOf(CharacterId npc); }                    // player number, -1 none
public readonly record struct PlayerMessage(int Player, string Text)
{ public const int Everyone = -1; public bool For(int player) => Player < 0 || Player == player; }
// [S1] PlayerState: keeps Faction, Exists, Money, Towns, Know, Camps, KnowCamp, SquadAi; per-human members move to sessions
public PlayerSession Local { get; }                         // sessions[0]
public IReadOnlyList<PlayerSession> Sessions { get; }       // ascending Number
public PlayerSession? Find(int player);
public PlayerSession Session(int player);                   // get or add; commands and Join only
public PlayerSession Join(int player, Guid guid, string name);
public PlayerSession? FindByGuid(Guid guid);
public int NextNumber();                                    // max Number + 1
public bool Inspects(CharacterId id);
// [S1] Squad
public int Owner { get; set; }                              // player number; 0 for NPC squads
// [S1] World
public PlayerSession SessionOf(SimCommand command);
public int OwnerOf(CharacterId id);                         // squad owner; -1 NPC or gone
public IReadOnlyList<CharacterId> Actors(SimCommand command, IReadOnlyList<CharacterId> listed);
public void Post(string message);  public void Post(int player, string message);  public void PostOwner(CharacterId who, string message);
public IReadOnlyList<PlayerMessage> TakeMessages();         // was IReadOnlyList<string>
public ProspectReport? Prospected { get; }                  // Player.Local.Prospected
// [S1] SquadManagement
public static List<Squad> PlayerSquads(World world);
public static List<Squad> PlayerSquads(World world, int owner);
public static Squad NewSquad(World world, GameDatabase db, Vector3 at, string factionName, int owner = 0);
// [S1] interface defaults (bodies by S2)
TradeView? ITradeSource.PublishTrade(int player) => player == 0 ? PublishTrade() : null;
ConversationView? IDialogueSource.PublishConversation(int player) => player == 0 ? PublishConversation() : null;
DialogueRequest? IDialogueSource.PublishRequest(int player) => player == 0 ? PublishRequest() : null;
// [S1] declared with defaults, filled by S4
public int CharacterSnapshot.Owner { get; init; } = -1;
public int WorldSnapshot.Viewer { get; init; }
public int WorldSnapshot.PlayerFaction { get; init; } = -1;
public long WorldSnapshot.Money { get; init; }
public IReadOnlyList<float> WorldSnapshot.Relations { get; init; } = [];
public long WorldSnapshot.RelationsRevision { get; init; }
public IReadOnlySet<CharacterId> WorldSnapshot.Talkable { get; init; } = NoIds;
public Production.ProspectReport? WorldSnapshot.Prospected { get; init; }
public bool WorldSnapshot.InCreation { get; init; }
public IReadOnlyList<string> WorldSnapshot.CreationRaces { get; init; } = [];
// [S1 stub, S5 body] PopulationSystem.Player.cs; called from Inputs (S1) and the paused path (S4)
internal void ApplyJoins(World world, IReadOnlyList<SimCommand> commands);
// [S3] CommandGate.cs
public sealed record GateOptions
{
    public bool Cheats { get; init; }
    public Func<Construction.PlannedBuilding, bool>? Placeable { get; init; }
    public int MaxList { get; init; } = 256; public int MaxPlans { get; init; } = 64; public int MaxText { get; init; } = 256;
}
public readonly record struct GateResult(SimCommand? Command, string? Refusal);
public static class CommandGate
{
    public static GateResult Admit(World world, int player, SimCommand command, GateOptions? options = null);
    public static IReadOnlySet<Type> Ruled { get; }
    public static bool HostOnly(Type type);
}
// [S4] World
public WorldSnapshot PublishFor(int player);                // host thread, Phase Outside; no side effects on hashed state
public bool IsReplica { get; }
public void MakeReplica(ulong seed);                        // systems empty and Tick 0, else InvalidOperationException; sets Seed
public void Present(WorldSnapshot previous, WorldSnapshot current);   // replica only; Tick = current.Tick; mirrors viewer facts (C9)
// [S5] PopulationSystem
public Squad AddPlayer(World world, int player, NewGameStart start, Vector3 near);
// [S6] SaveLoadOptions
public Guid LocalPlayer { get; init; }                      // Guid.Empty: single-player load
```

On a replica, `RunTick` and `ApplyPausedInterfaceCommands` throw `InvalidOperationException("a replica world never ticks")`,
`PreparePopulation` returns true, `CanTalk(id, _)` is `Snapshot.Talkable.Contains(id)`. `Present` sets
`Player.Faction/Money` and `Player.Local.{Selection (Selected characters), Inspected, Squad = ActiveSquad, InCreation,
CreationRaces, Prospected}` from `current`. None of it is hashed.

**Engine** (`Meitou.Engine`).

```csharp
// [E1] Time/ClockRequest.cs
public enum ClockRequestKind : byte { TogglePause, SetSpeed, Faster, Slower }
public readonly record struct ClockRequest(ClockRequestKind Kind, double Speed = 0)
{
    public static ClockRequest TogglePause => new(ClockRequestKind.TogglePause);
    public static ClockRequest Set(double speed) => new(ClockRequestKind.SetSpeed, speed);
    public static ClockRequest Faster => new(ClockRequestKind.Faster);
    public static ClockRequest Slower => new(ClockRequestKind.Slower);
    public void ApplyTo(SimulationClock clock);
}
public interface IClockAuthority { void Request(ClockRequest request); }
// [E1] WorldSession
public IClockAuthority? ClockAuthority { get; set; }
public bool ModalsPause => ClockAuthority is null;
public void RequestClock(ClockRequest request);             // authority, else request.ApplyTo(Simulation); the control tick's six lines use it
public event Action<World>? SimulationTicked;               // after each simulation tick
public event Action<World>? WorldReplaced;                  // after ReplaceWorld swapped (argument: the new world)
// [S4] WorldSession
public enum SessionMode : byte { Local, Replica }
public SessionMode Mode { get; }
public float SimulationAlpha { get; }                       // replica: presented alpha [0,1]
public float MotionAlpha { get; }                           // replica: [0,2] (extrapolation); local: Simulation.Alpha
public void Present(WorldSnapshot previous, WorldSnapshot current, float alpha, float motionAlpha);
public World ReplaceWorld(IEnumerable<ITickSystem> systems, Action<World>? setup = null, SessionMode mode = SessionMode.Local);
    // Replica: systems empty and setup must call MakeReplica (else ArgumentException); Advance then runs control ticks only.
```

**Net primitives and messages** (`Meitou.Net`) [N1]. Layout rules: D2 §2.

```csharp
public sealed class NetFormatException(string message) : Exception(message);
public sealed class NetWriter : IDisposable
{
    public NetWriter(int capacity = 256);
    public int Length { get; }  public ReadOnlySpan<byte> Written { get; }  public void Reset();  public void Truncate(int length);
    public void WriteByte(byte v); public void WriteBool(bool v); public void WriteVarUInt(ulong v); public void WriteVarInt(long v);
    public void WriteUInt16(ushort v); public void WriteUInt32(uint v); public void WriteUInt64(ulong v);
    public void WriteSingle(float v); public void WriteDouble(double v); public void WriteHalf(Half v);
    public void WriteUtf8(string v); public void WriteBytes(ReadOnlySpan<byte> v); public void WriteGuid(Guid v);
}
public ref struct NetReader
{
    public NetReader(ReadOnlySpan<byte> data);
    public int Remaining { get; }
    public byte ReadByte(); public bool ReadBool(); public ulong ReadVarUInt(); public uint ReadVarUInt32();
    public long ReadVarInt(); public int ReadVarInt32(); public ushort ReadUInt16(); public uint ReadUInt32(); public ulong ReadUInt64();
    public float ReadSingle(); public float ReadFinite(); public double ReadDouble(); public Half ReadHalf();
    public string ReadUtf8(int maxBytes); public ReadOnlySpan<byte> ReadBytes(int count); public Guid ReadGuid();
    public int ReadCount(int max); public T ReadEnum<T>() where T : struct, Enum; public void ExpectEnd();
}
public sealed class StringTableWriter(bool confirmOnSend)
{
    public void Write(NetWriter w, string? s, bool intern = true);
    public void BeginPart(); public int Mark(); public void Rollback(int mark);
    public IReadOnlyList<int> PartDefinitions { get; }  public void Confirm(IEnumerable<int> indices);
}
public sealed class StringTableReader { public string? Read(ref NetReader r, int maxBytes); }
public enum NetChannel : byte { Snapshots = 0, Control = 1, Views = 2, Events = 3 }
public interface IPacketSink { int MaxUnreliablePayload { get; } void Send(NetChannel channel, ReadOnlySpan<byte> packet); }
public enum MessageType : byte
{
    Challenge = 0x01, Hello = 0x02, Welcome = 0x03, Reject = 0x04, Ready = 0x05, WorldReset = 0x06, Roster = 0x07,
    ClockRequest = 0x08, ClockState = 0x09, Rules = 0x0A, Weather = 0x0B,
    SnapshotPart = 0x10, ClientFrame = 0x11, CommandBatch = 0x20, CommandResult = 0x21, ChatRequest = 0x22,
    ViewUpdate = 0x30, LookDefinition = 0x31, EventBatch = 0x40,
}
public static class NetProtocol
{
    public const ushort Version = 1;    // bump on any layout change in any codec
    public const int DefaultPort = 40120, MaxPlayers = 8, MaxNameLength = 24, MaxChatLength = 200, MaxPasswordLength = 32;
    public static ReadOnlySpan<byte> Magic => "MEITOU"u8;   // connect request payload: Magic + UInt16 Version
}
```

Channels: Snapshots (Sequenced) carries `SnapshotPart` h→c and `ClientFrame` c→h. Control (ReliableOrdered) carries
Challenge, Hello, Welcome, Reject, Ready, WorldReset, Roster, ClockRequest, ClockState, Rules, Weather, CommandBatch,
CommandResult, ChatRequest. Views (ReliableOrdered, h→c) carries ViewUpdate and LookDefinition. Events (ReliableOrdered,
h→c) carries EventBatch. String tables: one per channel and direction, kept for the whole connection (never reset at an
epoch); look ids are never reused within a `NetHost`, so the client's `LookCache` is kept for the connection too.

`NetLimits` = D2 §2 except `MaxChat = 1024` bytes, `MaxReliableMessage = 65536`, `MaxCommandBatch = 64`.

**Session value records** (`Meitou.Net.Session`) [N1; codecs N9]:

```csharp
public enum RejectCode : byte { Protocol, Build, Data, Password, Full, Banned, Duplicate, Kicked, HostClosed, Timeout,
    ProtocolError, NoSlot, LoadTimeout, Left, Unreachable }
public sealed record NetDisconnect(RejectCode Code, string Reason);
public enum ClockPolicy : byte { AnyPlayer, HostOnly }
public sealed record NetRules(int SendRate, ClockPolicy ClockPolicy, int MaxPlayers, bool Cheats, bool HasPassword, int Port);
public sealed record SessionRules(int AttackSlots, bool LateDefences, bool DoorsCloseBehind, bool KeepRoamers, bool OffscreenFights,
    bool OffscreenHealing, bool CampaignAncestorsOnce, bool KeepWild, bool LuckyrenMode);   // the UserConfig keys of the same names
public enum NetPlayerState : byte { Host, Loading, Playing, Away }
public sealed record RosterEntry(int Player, string Name, NetPlayerState State, int RoundTripMs, int Colour, int Squads);
public enum ClockChangeKind : byte { None, Paused, Resumed, Speed, TimeSet }
public readonly record struct ClockChange(int Player, ClockChangeKind Kind, double Speed);     // Player -1: none
public readonly record struct ClockState(ushort Epoch, double Speed, double LastSpeed, bool HostBusy, double AchievedSpeed,
    long AnchorTick, double AnchorHours, ClockChange Change);
public sealed record WeatherSync(int Revision, string? Forced, int Day, double Hour, IReadOnlyList<Meitou.Data.World.WeatherRegionSnapshot> Regions);
```

**Protocol** [N9] (`Meitou.Net.Session`). Reason texts and check order: D3 §3.

```csharp
public sealed record Challenge(byte[] Nonce, string HostName, bool NeedsPassword);                 // Nonce: 16 bytes
public sealed record Hello(ushort Protocol, string Build, Guid Player, string Name, byte[] Proof, DataSetIdentity Data, ulong AnimationHash);
public sealed record Welcome(int Player, NetRules Rules, IReadOnlyList<RosterEntry> Roster);
public sealed record Reject(RejectCode Code, string Reason);                                       // also the Drop payload
public sealed record Ready(ushort Epoch);
public sealed record WorldReset(ushort Epoch, int Player, ulong Seed, Vector3 Focus, ClockState Clock, SessionRules Rules, WeatherSync? Weather);
public sealed record RulesUpdate(NetRules Net, SessionRules Session);
public sealed record ChatRequest(string Text);
public static class ProtocolCodec   // one Write overload per record above, plus ClockRequest, ClockState and RosterEntry lists
{
    public static void Write(NetWriter w, Hello m);   /* ... */   public static Hello ReadHello(ref NetReader r);   /* ReadX per type */
}
public sealed record DataSetEntry(string Name, ulong Hash, long Length);
public sealed record DataSetIdentity(IReadOnlyList<DataSetEntry> Files)
{
    public ulong Combined { get; }
    public static DataSetIdentity Compute(LoadOrder order);
    public static string? Difference(DataSetIdentity host, DataSetIdentity client);
}
public sealed record NetIdentity(Guid PlayerId, string Name, string Build, DataSetIdentity Data);
public static class Handshake
{
    public static byte[] Proof(string? password, ReadOnlySpan<byte> nonce, Guid player);
    public static Reject? CheckRequest(ReadOnlySpan<byte> connectData, int connected, int maxPlayers, bool addressBanned);
    public static Reject? CheckHello(Hello hello, NetIdentity host, ulong hostAnimationHash, string? password, ReadOnlySpan<byte> nonce, bool allowBuildMismatch, bool banned);
    public static string SanitizeName(string raw, int number);
    public static ulong AnimationHash(AnimationLibrary library);   // Rng.StableHash over Definitions ids in order, plus count (D2 §3)
}
public static class PlayerColourRule
{   /// <summary>The host is 0; else `preferred` (number % 8) when free, else the lowest free; -1 when all 8 are taken.</summary>
    public static int Assign(IReadOnlyCollection<int> taken, int preferred);
}
```

**Commands** [N2] (`Meitou.Net.Commands`). Tag table and layout: D2 §4, plus `241 JoinPlayer (H)`, `242 TransferSquad (H)`.

```csharp
public enum CommandOrigin : byte { Client, Debug, HostOnly }
public static class CommandTags
{
    public static IReadOnlyList<(ushort Tag, Type Type, CommandOrigin Origin)> All { get; }
    public static ushort TagOf(Type type);  public static (Type Type, CommandOrigin Origin) Of(ushort tag);
}
public static class CommandCodec
{
    public static void Write(NetWriter w, StringTableWriter strings, SimCommand command);
    public static SimCommand Read(ref NetReader r, StringTableReader strings, long tick, int player, bool allowDebug);
}
public sealed record CommandBatch(ushort Epoch, uint FirstSeq, IReadOnlyList<SimCommand> Commands);
public sealed record CommandResult(uint Seq, string Reason);
public static class CommandBatchCodec
{
    public static void Write(NetWriter w, StringTableWriter strings, CommandBatch batch);
    public static CommandBatch Read(ref NetReader r, StringTableReader strings, long tick, int player, bool allowDebug);
    public static void Write(NetWriter w, CommandResult result);
    public static CommandResult ReadResult(ref NetReader r);
}
```

**Replication** [N4–N7 codecs, N10 delta] (`Meitou.Net.Replication`). Groups, view kinds, encodings: D2 §5–§7, with:
`CharacterSnapshot.Owner` in group 9 (Identity, zigzag); view kind 1 (Header) = SquadAi, ActiveSquad, Viewer, PlayerFaction,
Money, InCreation, CreationRaces; new kinds 23 Relations (Relations + RelationsRevision, structural), 24 Talkable (id set),
25 Prospected (`ProspectReport?`). The part header is
`0x10 | Epoch u16 | Seq | Tick | ViewSeq | PartIndex | PartCount | removals | projectiles | characters`;
ViewUpdate is `0x30 | Epoch u16 | kind | ViewSeq | Tick | body`; LookDefinition `0x31 | LookId | recipe kind | recipe`.

```csharp
public readonly record struct ViewState(Vector3 Eye, Vector3 Focus, float DrawDistance);
public sealed record ClientFrame(ushort Epoch, uint FrameSeq, IReadOnlyList<(uint Seq, ulong Parts)> Acks, ViewState View, IReadOnlyList<int> Resync);
public sealed record ReplicaCatalog(GameDatabase Database, AnimationLibrary Animations, IAppearanceSource Appearances);
public sealed record SnapshotEncoderOptions
{
    public int SendHz { get; init; } = 20; public int BytesPerSecond { get; init; } = 262_144; public int BaselineWindow { get; init; } = 60;
    public int History { get; init; } = 128; public int KeepAliveHz { get; init; } = 5;
}
public enum ViewKind : byte { /* D2 §6 kinds 1-22, then */ Relations = 23, Talkable = 24, Prospected = 25 }   // [N1]
public readonly record struct ItemCodec<T>(ItemWriter<T> Write, ItemReader<T> Read);                           // [N1]
public sealed class KeyedViewSync<TKey, TItem>(ViewKind kind, Func<TItem, TKey> key, ItemCodec<TItem> codec, TimeSpan minInterval) where TKey : notnull
{ public bool Encode(IReadOnlyList<TItem> now, bool structural, TimeSpan time, NetWriter w, StringTableWriter s); }   // [N1]
public sealed class RecordingAppearances(IAppearanceSource inner) : IAppearanceSource { public AppearanceRecipe? RecipeOf(CharacterAppearance look); }  // [N7]
public abstract record AppearanceRecipe { public static AppearanceRecipe? FromLook(CharacterAppearance look); public CharacterAppearance? Build(IAppearanceSource source, GameDatabase db); }  // [N7]
public sealed class LookRegistry(RecordingAppearances recording) { public uint IdOf(CharacterAppearance? look); public AppearanceRecipe? RecipeOf(uint id); }  // [N7]
public interface ILookResolver { CharacterAppearance? Resolve(uint lookId); void Define(uint lookId, AppearanceRecipe recipe); }  // [N7]
public sealed class LookCache(IAppearanceSource source, GameDatabase db) : ILookResolver;                       // [N7]
public sealed class ClientReplicator                                                                          // [N10]
{
    public ClientReplicator(int player, SnapshotEncoderOptions options, LookRegistry looks, AnimationLibrary animations);
    public ushort Epoch { get; }
    public void Reset(ushort epoch);                 // no baselines, every view resent; the sent-look set is kept
    public void Send(WorldSnapshot view, TimeSpan now, IPacketSink sink);   // looks, then views (Views), then parts (Snapshots)
    public void OnClientFrame(ClientFrame frame);    // frames of another epoch ignored
    public IReadOnlySet<CharacterId> Interest { get; }
    public ViewState View { get; }
}
public sealed class ClientMirror(AnimationLibrary animations, ILookResolver looks)                              // [N10]
{
    public ushort Epoch { get; }
    public void Reset(ushort epoch);
    public void Receive(NetChannel channel, ReadOnlySpan<byte> packet);  // Snapshots: a bad part is dropped; Views: NetFormatException propagates
    public bool TryAssemble(out WorldSnapshot snapshot);
    public long TickOf(CharacterId id);
    public void WriteFrame(NetWriter w, ViewState view);                 // ClientFrame with the pending acks
}
```

**Events** [N8] (`Meitou.Net.Events`). Field encodings: D2 §8.

```csharp
public abstract record NetEvent;
public sealed record SoundNet(SoundEvent Sound) : NetEvent;            // kind 1, spatial
public sealed record BloodNet(BloodMark Mark) : NetEvent;              // kind 2, spatial
public sealed record FloaterNet(Floater Floater) : NetEvent;           // kind 3, spatial
public sealed record MessageNet(string Text) : NetEvent;               // kind 4
public sealed record KnockoutNet(StealthKoEntry Entry) : NetEvent;     // kind 5 (kind 6 reserved, was ProspectNet)
public sealed record ChatNet(int From, string Name, string Text) : NetEvent;   // kind 7
public enum NoticeKind : byte { Joined, Left, TimedOut, Kicked, Banned }
public sealed record NoticeNet(NoticeKind Kind, int Player, string Name, string Detail) : NetEvent;   // kind 8
public sealed record EventBatch(ushort Epoch, long Tick, IReadOnlyList<NetEvent> Events);             // ≤ 1024 events
public static class EventCodec { public static void Write(NetWriter w, EventBatch batch); public static EventBatch Read(ref NetReader r); }
public static class EventFilter
{
    public static bool IsSpatial(NetEvent e);
    public static bool Wants(NetEvent e, ViewState view, IReadOnlySet<CharacterId> interest, float radius);
}
```

**Transport** [N3] (`Meitou.Net.Transport`): D3 §2 verbatim, with `NetChannel` from `Meitou.Net`, `NetDisconnect`
unaffected, and `LiteNetTransport` written against LiteNetLib 2.1.4 (`NetManager` with `ChannelsCount = 4`, Snapshots =
`DeliveryMethod.Sequenced`, the others `ReliableOrdered`, `DisconnectTimeout = 15000`). `LoopbackNetwork`,
`LoopbackOptions`, `INetClock`, `StopwatchNetClock`, `ManualNetClock`: D3 §2. `PeerSink(INetTransport, NetConnectionId) :
IPacketSink` adapts one peer.

**Host and client** [N11, N12] (`Meitou.Net.Session`):

```csharp
public interface IHostGame                    // implemented by GameHost (G2) and by test stubs
{
    GateOptions Gate { get; }                 // Cheats (lobby), Placeable (host placement check)
    SessionRules Rules { get; }               // compared by value each EndFrame; a change is broadcast as Rules
    WeatherSync? Weather();                   // null: none; sent in WorldReset, on Revision change and every 60 s
}
public sealed record NetHostOptions
{
    public int Port { get; init; } = NetProtocol.DefaultPort;  public int MaxPlayers { get; init; } = 4;   // 2..8, host included
    public string? Password { get; init; }  public string HostName { get; init; } = "Meitou";
    public ClockPolicy ClockPolicy { get; init; } = ClockPolicy.AnyPlayer;
    public int SendRate { get; init; } = 20;  public int PausedSendRate { get; init; } = 5;
    public int MaxBytesPerSecond { get; init; } = 262_144;  public float EventRadius { get; init; } = 10_000;
    public bool AllowBuildMismatch { get; init; }
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);  public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(180);
}
public readonly record struct NetPeerStats(int RoundTripMs, float LossPercent, int BytesInPerSecond, int BytesOutPerSecond, int LastSnapshotBytes);
public sealed record NetPlayerInfo(int Number, Guid Id, string Name, NetPlayerState State, int Colour, NetPeerStats Stats);
public sealed class NetHost : IClockAuthority, IDisposable
{
    public NetHost(WorldSession session, INetServerTransport transport, NetHostOptions options, NetIdentity self,
        IHostGame game, ReplicaCatalog catalog, INetClock? clock = null);
    public void Start();                      // Listen (SocketException propagates); Local.Guid/Name = self; ClockAuthority = this; RequestedPause = false
    public void BeginFrame();                 // immediately before session.Advance: Poll, then the tap hooks the world's queues
    public void EndFrame(bool hostBusy);      // after Advance and WorldNews: tap drain, clock, events, snapshots, roster; hostBusy = GameHost.Covered
    public NetRules Rules { get; }
    public IReadOnlyList<RosterEntry> Roster { get; }      // host first; Away entries kept
    public IReadOnlyList<NetPlayerInfo> Players { get; }
    public void Send(int player, NetEvent e);              // non-spatial; -1: every connected remote player
    public void SendChat(string text);                      // as player 0
    public void Kick(int player, string reason);
    public void Ban(int player, string reason);            // GUID ban for this NetHost's lifetime
    public event Action<NoticeNet>? Notice;                 // joined/left/timed out/kicked/banned (also sent to clients)
    public event Action<ChatNet>? Chat;
    public event Action<ClockChange, string>? ClockChanged; // every change, the host's own included, with the player's name
    void IClockAuthority.Request(ClockRequest request);     // the host's own input, as player 0
    public string StatsLine();
    public void Stop(string reason);                        // Drop every peer with HostClosed + reason; ClockAuthority = null
    public void Dispose();                                  // Stop("The host ended the game.")
}
public enum NetClientState : byte { Idle, Connecting, Authenticating, Joined, Playing, Closed }
public readonly record struct NetClientStats(int RoundTripMs, float LossPercent, int BytesInPerSecond, int BytesOutPerSecond,
    int DelayMs, int Starved, double SnapshotAgeSeconds);
public sealed class NetClient : IClockAuthority, IDisposable
{
    public NetClient(INetClientTransport transport, NetIdentity self, ReplicaCatalog catalog, INetClock? clock = null);
    public NetClientState State { get; }  public int Player { get; }  public ushort Epoch { get; }
    public NetRules? Rules { get; }  public SessionRules? SessionRules { get; }  public NetDisconnect? Closed { get; }
    public IReadOnlyList<RosterEntry> Roster { get; }  public NetClientStats Stats { get; }
    public bool HasPresented { get; }                       // a snapshot of this epoch is on show
    public void Connect(string host, int port, string? password);
    public void Attach(WorldSession replica);               // Mode must be Replica; sets replica.ClockAuthority = this
    public void Ready();                                    // sends Ready(Epoch); State = Playing
    public void SetView(ViewState view);
    public void BeginFrame(double realSeconds);             // Poll; present; release due events; follow the clock
    public void EndFrame();                                 // uplink: commands from the replica's queue, ClientFrame
    public void SendChat(string text);
    public void Leave();                                    // Drop(Reject(Left)); State = Closed
    void IClockAuthority.Request(ClockRequest request);
    public event Action<WorldReset>? WorldReset;            // first one = the join; later ones = host world swaps
    public event Action<NetDisconnect>? Disconnected;
    public event Action<ChatNet>? Chat;  public event Action<NoticeNet>? Notice;  public event Action<KnockoutNet>? Knockout;
    public event Action<string, bool>? Message;             // MessageNet (false) and CommandResult refusals (true: error)
    public event Action<ClockChange, string>? ClockChanged;
    public event Action<WeatherSync>? Weather;  public event Action<RulesUpdate>? RulesChanged;  public event Action? RosterChanged;
}
```

**Game ↔ UI** (`Meitou.Game`) [U1]:

```csharp
public enum NetRole { None, Host, Client }
public enum NetPhase { Idle, Connecting, Joining, Loading, Playing, Ended }
public enum MultiplayerEventKind { Joined, Left, TimedOut, Kicked, Banned, Paused, Resumed, Speed, Chat }
public readonly record struct MultiplayerEvent(MultiplayerEventKind Kind, int Player, string Name, string Text = "", double Speed = 0);
public readonly record struct NetQuality(int PingMs, double SnapshotAgeSeconds, float Loss);
public interface IMultiplayerView                    // implemented by NetSessionView (G1)
{
    NetRole Role { get; }  NetPhase Phase { get; }  int LocalPlayer { get; }
    IReadOnlyList<RosterEntry> Players { get; }      // roster; lists show State != Away
    NetRules Rules { get; }  NetQuality Quality { get; }  NetDisconnect? Ended { get; }
    int? PausedBy { get; }
    bool CanControlTime(int player);                 // Rules.ClockPolicy == AnyPlayer || player == 0
    IReadOnlyList<MultiplayerEvent> TakeEvents();
    void SendChat(string text);
    void Kick(int player, string reason);  void Ban(int player);   // host only; no-op on a client
}
public sealed record HostSettings(int Port, int MaxPlayers, string Password, bool ClientsControlTime, bool AllowCheats);  // "" = no password
public abstract record MultiplayerRequest;
public sealed record HostGame(HostSettings Settings, string? NewGameStart, string? SaveFolder) : MultiplayerRequest;  // both null: the running game
public sealed record JoinGame(string Host, int Port, string Password) : MultiplayerRequest;
public sealed record CancelJoin() : MultiplayerRequest;
public sealed record StopHosting() : MultiplayerRequest;
public sealed record LeaveGame(bool Quit) : MultiplayerRequest;
public static class MultiplayerDefaults
{ public const int Port = NetProtocol.DefaultPort, MaxPlayers = 4, MaxPlayersLimit = NetProtocol.MaxPlayers, NameLength = 24, ChatLength = 200, PasswordLength = 32; }
public static class JoinAddress { public static bool TryParse(string text, int defaultPort, out string host, out int port); }  // host, host:port, [v6], [v6]:port, bare v6
// PlayerInterface
public IMultiplayerView? Net { get; set; }
public event Action<MultiplayerRequest>? MultiplayerRequested;
internal UserConfig? Config { get; set; }
public bool PlayerListHeld { get; set; }
public void RefreshMenuPause();
public void ShowDisconnected(NetDisconnect reason, bool reachedPlay);   // title: DISCONNECTED when reachedPlay, else CONNECTION FAILED
public void OpenMultiplayer(bool runningGame);
// UserConfig (no version bump)
public string? PlayerName { get; set; }  public Guid PlayerId { get; set; }  public Guid EnsurePlayerId();
public int HostPort { get; set; } = NetProtocol.DefaultPort;  public int HostMaxPlayers { get; set; } = 4;
public bool HostClientsControlTime { get; set; } = true;  public bool HostAllowCheats { get; set; }
public List<RecentGame> RecentGames { get; set; } = [];    // newest first, ≤ 8, unique by Address
public static UserConfig LoadOrCreate(string file);
// SettingsMenu [G5]
public Func<SessionRules?>? HostRules { get; init; }      // non-null on a client: rule rows read it and are locked
```

**GameHost seams** [G1] (partial methods; G2/G3 fill them in their own files):

```csharp
NetRole role; NetHost? netHost; NetClient? netClient; NetSessionView? netView;
public HostSettings? HostOnStart { get; init; }   public JoinGame? JoinOnStart { get; init; }   public string? NameOverride { get; init; }
NetIdentity NetIdentity();              // PlayerId = config.EnsurePlayerId(); Name = SanitizeName(NameOverride ?? PlayerName ?? user); Build = BuildVersion; Data computed once on a worker
void NetBeginFrame(double dt);          // netHost?.BeginFrame(); netClient?.BeginFrame(dt)
void NetEndFrame();                     // netHost?.EndFrame(Covered); netClient?.EndFrame(); ClientFrameCore()
partial void StartHostingCore(HostSettings settings, string? newGameStart, string? saveFolder);   // G2
partial void StopHostingCore(string reason);                                                     // G2
partial void BeginJoinCore(JoinGame target);                                                     // G3
partial void LeaveClientCore(NetDisconnect reason, bool quit);                                   // G3
partial void ClientFrameCore();                                                                  // G3: relations row, creation, SetView
```

## 3. End-to-end flows

**Frame order** (`GameHost.Interactive.cs`, G1):
`DoEvents → CoveredFrame → … → NetBeginFrame(frameDt) → if (!Covered) session.Advance(frameDt) → DuelHoldClock → WorldNews →
NetEndFrame() → HandleSaves → HandleNewGame → HandleMainMenu → StepSceneLoad → …`. Only `session.Advance` ticks the world;
all networking runs on the frame thread (LiteNetLib events only inside `Poll`; its own threads keep connections alive while a
long scene-load stage blocks the loop, R8).

**Host start.** UI raises `HostGame` (Esc MULTIPLAYER → running game; title → new game or save). G2 runs the existing new-game
or load flow when asked, then waits for `HostReady` (`!Covered && sceneLoad is null && Player.Exists && population != null &&
!EditorOpen && pendingCreation is null && !creationWarmOwed && !IsDuel && !IsSandbox`). Then:
`netHost = new NetHost(session, new LiteNetTransport(), options, NetIdentity(), this, catalog)`; `Start()` (a
`SocketException` → error notice, stay single-player). `Start` sets `Local.Guid/Name`, `ClockAuthority`,
`RequestedPause = false`; G2 sets `role = Host`, `player.Net = netView`, `player.RefreshMenuPause()`, saves the settings.

**Client join.**
1. UI `JoinGame` → G3 `BeginJoinCore`: scene load stages "connect" (`new NetClient(...).Connect`, 10 s), "host" (wait for the
   first `WorldReset`, 60 s), "world", "people" (`HasPresented`), "finishing". A failure before "world" shows the reason in the
   Multiplayer window (D5 A5); later failures go through `ShowDisconnected`.
2. Transport connect with payload `Magic + Version` → host `CheckRequest` (protocol, full, address ban) → `Challenge(nonce,
   name, needsPassword)` → client `Hello(Version, Build, PlayerId, Name, Proof, Data, AnimationHash)` → host `CheckHello`
   (build, data difference, animation hash, password, GUID ban). Number = `FindByGuid ?? NextNumber` (a client GUID equal to
   the host's, or a number > 255 → refuse `Duplicate`/`NoSlot`); a live peer with that GUID is dropped (`Duplicate`).
   `world.Player.Join(n, guid, SanitizeName(name))` (duplicate names get " (2)"); peer `Loading`; colour assigned; replicator
   created; `Welcome(n, rules, roster)` then `WorldReset(epoch, n, seed, FocusOf(n), clock, game.Rules, game.Weather())`;
   roster broadcast; `NoticeNet(Joined)` to everyone, `Notice` raised on the host.
3. Client "world" stage: `session.ReplaceWorld([], w => w.MakeReplica(reset.Seed), SessionMode.Replica)`; G3 nulls
   `population/aggro/playerSquad/loadedSave/preparedBase`, unloads nav zones, sets the clock from the anchor, places the camera
   on `reset.Focus`, applies weather and rules; `netClient.Attach(session)`; `Ready()`; `BeginWarm()`.
4. Host on `Ready(epoch)`: peer `Playing`; `replicator.Reset(epoch)`; enqueue `JoinPlayer(null, JoinPoint) { Tick =
   world.Tick, Player = n }` (taken by the next tick, or by the paused path). Snapshots, views and events start.
5. Client: first assembled snapshot → jitter buffer → `Present` → `HasPresented` → warm-up readiness → "finishing":
   `player.Reset(); CloseFrontend(); FollowSelected()`, notice "Joined {host}'s game", `RecentGames` updated.
6. New player: `AddPlayer` made a Wanderer squad owned by n with `InCreation`. G3 sees `snapshot.InCreation` and its own
   active squad, opens the creation editor; `EditCharacter`/`FinishCreation` travel the command path. Rejoin: the existing
   squad is selected, no editor.

**Command path.** UI code enqueues as today (`session.World.Commands.Enqueue(c with Tick = session.World.Tick)`). Host: Player 0,
no gate. Client: `EndFrame` takes `Commands.TakeDue(long.MaxValue)` and, when `Playing`, sends `CommandBatch(epoch, seq, ≤ 64)`
on Control (else drops them). Host, on receipt: epoch matches and peer `Playing`; token bucket 200/s, burst 400 (excess
dropped, ≤ 1 notice/s); `CommandCodec.Read(…, tick: world.Tick, player: n, allowDebug: game.Gate.Cheats)` (a malformed
batch → drop the peer with `ProtocolError`); `CommandGate.Admit(world, n, cmd, game.Gate)`; a refusal → `CommandResult(seq,
reason)` (≤ 2/s) → client `Message(reason, true)` → error notice; else `world.Commands.Enqueue(result.Command)`, applied next
tick or, for the paused filter's types, by `ApplyPausedInterfaceCommands`.

**Snapshot path.** Host `EndFrame` (not busy), per `Playing` peer when `now ≥ NextSendUs`:
`snap = world.PublishFor(n)` (core reused by state stamp) → `replicator.Send(snap, now, sink)`: interest (D2 §7) → missing
looks as `LookDefinition` (Views) → changed views as `ViewUpdate` (Views) → parts on Snapshots, deltas against each entity's
acked state within `BaselineWindow`, budget `MaxBytesPerSecond / SendRate`. `NextSendUs += 1 s / (paused ? PausedSendRate :
SendRate)`. Client `BeginFrame`: `Poll` → `mirror.Receive` (parts of another epoch dropped) → `TryAssemble` →
`jitter.Add(tick, arrivalUs, snapshot)`; `EndFrame` sends `ClientFrame` (acks of the last ≤ 8 Seqs + `ViewState` + Resync)
when parts arrived since the last one, else every 100 ms → host `OnClientFrame` moves baselines forward and confirms string
definitions. Presentation: `jitter.Advance(now, dt)` (D3 §5, frames are `WorldSnapshot`s) → teleport snap → 
`session.Present(older, newer, alpha, motionAlpha)` → `World.Present` (snapshots, Tick, mirrored facts) → renderer reads
`CurrentSnapshot/PreviousSnapshot/SimulationAlpha/MotionAlpha`. Views commit when an assembled snapshot has `Tick ≥ view.Tick`.

**Events path.** Host `BeginFrame` (after `Poll`): the tap remembers `world.Sounds/BloodMarks/Floaters` (unless they are its
own) and installs its own queues. `EndFrame`: the tap dequeues everything, re-enqueues each item into the remembered
consumer queue when non-null, and restores the consumer queues (also on a world `ReplaceWorld` handed the tap's queue). Per
`Playing` peer: spatial events passing `EventFilter.Wants(e, peer.View, replicator.Interest, EventRadius)` plus the queued
targeted events (`Send`) go out as one `EventBatch(epoch, world.Tick, …)` on Events when non-empty. Client: spatial events
are released into the replica world's own queues (`Sounds?.Enqueue`, null = consumer off) when the presented tick reaches
`batch.Tick` or after 1 s; non-spatial ones are raised at once (`Chat`, `Notice`, `Message`, `Knockout`). Host-side news
(G1 `WorldNews`): `m.For(0)` → local notice and log; `m.Player > 0` → `Send(m.Player, MessageNet)`; `m.Player < 0` →
`Send(-1, MessageNet)`. Knockouts: `owner = world.OwnerOf(k.Attacker)`; 0 → today's notice; > 0 → `Send(owner,
KnockoutNet(k))`; one `KnockoutText(name, k)` builds both texts.

**Clock path.** Space/F2–F4 (control tick) and the HUD buttons call `session.RequestClock(r)`. Single-player: applied as
today. Host: `NetHost.Request` → `ApplyClock(0, r)`. Client: a `ClockRequest` message (U1 first refuses locally when
`!CanControlTime`). `ApplyClock(n, r)`: `HostOnly && n != 0` → `MessageNet("Only the host can change the game speed.")`;
else apply to the host `SimulationClock`; unchanged → nothing; else `ClockChange(n, Paused|Resumed|Speed, speed)`, raise
`ClockChanged`, mark dirty. `EndFrame` broadcasts `ClockState` when dirty, when `hostBusy` changed, when the game clock was
set (anchor mismatch → `TimeSet`), else every 2 s. Client: stores it and each `BeginFrame` sets its mirror clock
(`SetSpeed(LastSpeed)`, `SetSpeed(0)` when paused, `RequestedPause = HostBusy`), sets the jitter rate (`30 · AchievedSpeed`,
0 when paused or busy) and moves `session.Clock` to `AnchorHours + (presentedTick − AnchorTick)/30/SecondsPerGameHour`
(advance when 0 ≤ d < 3600 s, else `SetTime`); a change with `Player ≥ 0` raises `ClockChanged(change, name)`. G1's view
turns `ClockChanged` into `MultiplayerEvent`s and `PausedBy`; U3/U5 show "<name> paused the game" ("You" for oneself).
Modal windows never pause while `ClockAuthority` is set.

**Chat.** `IMultiplayerView.SendChat` → host `NetHost.SendChat` / client `ChatRequest` (Control). Host: strip control chars,
clamp to 200 chars, 5 lines per 5 s per player, then `ChatNet(from, name, text)` to every connected peer (Loading included)
and the host's `Chat` event. Own lines come back the same way, so every screen shows the host's order.

**Disconnect and reconnect.** Client `Leave` → `Drop(Reject(Left))`; a timeout → `TimedOut`; kick → `Drop(Reject(Kicked,
"Kicked by the host: {reason}"))`; ban → GUID banned for the session, `Banned`. Host side: the peer becomes `Away` (number,
session and squads kept; squads finish their orders); `HostWorldLink.PlayerLeft` enqueues `CloseTrade`, `EndConversation`,
`CloseContainer` with `Player = n`; roster and `NoticeNet` go out. Reconnect with the same GUID: same number, `Join`
refreshes the name, new replicator (full snapshot), `Ready` → `JoinPlayer` → rejoin path selects the squad. Host closes
(`Stop`/`Dispose`, main menu, quit): every peer gets `HostClosed`; clients `LeaveClientCore` → title + `ShowDisconnected`.
Stop hosting while playing: same for clients; the host continues single-player (`ClockAuthority = null`), extra sessions stay.

**World swap while hosting** (load, new game). `WorldReplaced` → `epoch++`; the tap re-hooks; for each connected peer:
`n = FindByGuid ?? NextNumber`, `Join(n, guid, name)` on the new world, `replicator.Reset(epoch)`, state `Loading`, a pending
`WorldReset` sent at the next `EndFrame` (after the caller set the game clock). While `Covered`, `EndFrame` sends only
Control traffic (`HostBusy = true`). Clients rebuild their replica (step 3 above, as a covered reload) and send `Ready`.
Main menu while hosting: G2 calls `StopHostingCore("The host ended the game.")` first.

**Save/load with owners.** Saves are the host's (clients: F5/F9 → "Only the host can save or load."; menu rows hidden).
Capture (S6): roster keys only when some session has a GUID; each player platoon gets `meitou owner` = its owner's GUID;
single-player saves stay byte-identical. Load while hosting: `SaveLoadOptions.LocalPlayer = config.PlayerId` → the local GUID
becomes 0, others keep their numbers, squads get their owners; then the world-swap flow. Load in single-player
(`LocalPlayer = Guid.Empty`): every squad goes to 0 (engine choice). A successful host save sends `MessageNet("The host saved
the game.")` to everyone.

## 4. Implementation waves

Rules for every package: own git worktree and branch off the previous wave's merged master; touch only the listed files;
`dotnet build` with warnings as errors; `dotnet test --filter "Category!=Slow&Category!=Bench"` green before merge and the
full run before the wave's last merge; `GoldenHashTests` unchanged (any moved pin means the change is wrong); docs for the
package in the same change. "Approved" tests fall under the fixed approved categories: (a) codec round-trips, (b) in-process
host+client over a memory transport (ownership, rejection, replicated view matches host), (c) per-player isolation,
(d) save round-trip of squad owners. Tests marked **ask** need the user's approval first (R13). Tier: **strong** =
complex/high-risk model; **fast** = mechanical. Size = estimated changed lines.

### Wave 1 (3 packages)

| Id | Title | Files | Depends | Acceptance | Tier, size, detail |
|---|---|---|---|---|---|
| S1 | Simulation foundation: sessions, owners, issuer | Create `Simulation/PlayerSession.cs`. Modify `PlayerState.cs`, `Commands.cs`, `World.cs`, `World.Prospecting.cs`, `Squads.cs`, `SquadManagement.cs`, `MovementSystem.cs`, `Orders.cs`, `BodySystem.cs`, `Items/Containers.cs`, `Items/PickUp.cs`, `Carrying/CarrySystem*.cs`, `Construction/ConstructionSystem.cs`, `FirstAid/FirstAidSystem.cs`, `Locks/LockSystem*.cs`, `Production/ProductionSystem*.cs`, `Crime/ArrestSystem*.cs`, `DebugCommands.cs`, `Editing/CharacterEditing.cs`, `PopulationSystem.cs`, `PopulationSystem.Loading.cs`, `PopulationSystem.Player.cs` (Local, `ApplyJoins` stub), `PopulationSystem.Sandbox.cs`, `PopulationCampaigns.cs`, `Trading/TradeSystem.cs` + `Dialogue/DialogueSystem.cs` (interface defaults only), `Dialogue/DialogueSystem.Recruit.cs`, `WorldPublish.cs` (Local only), `WorldSnapshot.cs` (new members declared), `Saving/SaveCapture.cs` + `Saving/SaveLoader.cs` (Local only); Game: `GameHost.DebugMenu.cs`, `GameHost.Sandbox.cs`, `GameHost.Saving.cs`, `PlayerInterface.Editor.cs`, `GameHost.Interactive.cs` (WorldNews `m.For(0)`), `GameHost.Screenshot.cs`; test renames (JailSave, QuickSave, SaveWorld, FirstAid, Player, FaithfulInteraction, TradeUi, Aggro); `docs/simulation.md` (Multiplayer section with placeholders for S2–S5) | none | Full suite; goldens unchanged. Approved (c): `Simulation/MultiplayerSessionTests.cs` (independent selections, empty-list MoveOrder uses the issuer's selection, Prune never picks another owner's character). Ask: per-owner squad limits/KeepActive, conditional hash terms, PayBail payer | strong, ~1800 (mandated single package), D1 §1–2 |
| N1 | Meitou.Net project and primitives | Create `src/Meitou.Net/Meitou.Net.csproj`, `Primitives/*`, `Fcs/FcsRecordCodec.cs`, `Messages/{MessageType,NetChannel,IPacketSink,NetProtocol}.cs`, `Session/SessionTypes.cs` (value records of §2b), `Replication/Views/{ViewKind,KeyedViewSync,ItemCodec}.cs`, `docs/net.md` (skeleton: one heading + placeholder per N package); modify `MeitouClient.slnx`, `tests/Meitou.Tests/Meitou.Tests.csproj`, `src/Meitou.Game/Meitou.Game.csproj` (ProjectReference), `THIRD_PARTY_NOTICES.md`, `docs/README.md` | none | Build; quick run. Approved (a): `Net/PrimitivesTests.cs` (varint/zigzag edges, NaN/−0 raw floats, string-table define/ref/literal/rollback/confirm round-trips), `Net/FcsRecordCodecTests.cs` (full round-trip). Ask: every `NetReader` failure case, duplicate FCS keys | fast, ~1100, D2 §2 |
| E1 | Engine clock seam | Create `Meitou.Engine/Time/ClockRequest.cs`; modify `Meitou.Engine/WorldSession.cs` (ClockAuthority, ModalsPause, RequestClock, control-tick lines, SimulationTicked, WorldReplaced), `PlayerInterface.Hud.cs` (PressButton → RequestClock), `PlayerInterface.Faithful.cs` (UpdateMenuPause → ModalsPause), `docs/engine.md` ("Loop": clock input) | none | Quick run incl. `Engine/SimulationClockTests`, `Engine/TimeTests`, `Engine/InputTests`, `Ui/FaithfulInteractionTests`; no new tests | fast, ~150, D3 §6 |

### Wave 2 (14 packages; all after wave 1)

| Id | Title | Files | Depends | Acceptance | Tier, size, detail |
|---|---|---|---|---|---|
| S2 | Dialogue and trade per player | `Dialogue/DialogueSystem*.cs`, `Trading/TradeSystem.cs`, `docs/simulation.md` (own subsection) | S1 | `DialogueTests`, `TradeTests`, `SurgeryTests`, goldens. Approved (c): `Simulation/MultiplayerWindowsTests.cs` (D1 §10 WP2 list) | strong, ~700, D1 §3 |
| S3 | Command gate | Create `Simulation/CommandGate.cs`; `docs/simulation.md` (own subsection) | S1 | Build; quick run. Ask: `Simulation/CommandGateTests.cs` (D1 §10 WP3 list; recommended, it is the trust boundary) | strong, ~700, D1 §4 |
| S4 | Per-viewer publish and replica world | `WorldPublish.cs`, `World.cs`, `World.Interface.cs` (stamp, replica guard, `JoinPlayer`/`TransferSquad` in the paused filter + `ApplyJoins` call), `World.Loading.cs`, `Meitou.Engine/WorldSession.cs` (SessionMode, Present, alphas, ReplaceWorld mode), `GameHost.Characters.cs` (MotionAlpha, looping-clip unwrap), `docs/simulation.md` (own subsection) | S1, E1 | Goldens; `PlayerTests`, `DialogueTests`, `Engine/*`. Approved (c): `Simulation/PublishForTests.cs` player 1's overlay holds only their selection, `Owner` right. Ask: `PublishFor(0)` equals `Snapshot` except Talkable; heal floaters and `StateHash` unchanged by `PublishFor`; replica guards | strong, ~900, D1 §5, D3 §6 |
| S5 | Joining and squad transfer | `PopulationSystem.Player.cs` (`AddPlayer`, `BuildStart` refactor, `ApplyJoins` body), `PopulationSystem.cs` (Inputs case), `SquadManagement.cs` (TransferSquad case), `docs/simulation.md` (own subsection) | S1 | Goldens (StartPlayer unchanged), `PopulationTests`. Ask: `Simulation/PlayerJoinTests.cs` (D1 §10 WP5 list + join while paused) | strong, ~500, D1 §6 |
| S6 | Save roster and owners | `Saving/SaveCapture.cs`, `Saving/SaveLoader.cs`, `docs/formats/save.md` | S1 | `QuickSaveTests`, `SaveWorldTests`, `JailSaveTests`, `SaveWriteTests`. Approved (d): `Save/MultiplayerSaveTests.cs` (owners round-trip with remap; single-player save has no new keys; empty `LocalPlayer` gives every squad to 0) | strong, ~400, D1 §7 |
| N2 | Command codec | `Net/Commands/*` | N1, S1 | Approved (a): `Net/CommandCodecTests.cs` (D2 §4 completeness and round-trip; HostOnly and Debug refusal) | fast, ~900, D2 §4 |
| N3 | Transport | `Net/Transport/*` (INetTransport family, PeerSink, LiteNetTransport on 2.1.4, LoopbackNetwork, clocks) | N1 | Build. Ask: `Net/LoopbackTransportTests.cs` | strong, ~700, D3 §2 |
| N4 | Snapshot core wire | `Net/Replication/Snapshot/*` (CharacterWire incl. Owner, animation, ragdoll, projectile codecs) | N1, S1 | Approved (a): `Net/CharacterWireTests.cs` (round-trip, `Quantize∘ToSnapshot` idempotent, quant precision) | fast, ~1000, D2 §5 |
| N5 | World and production views | `Net/Replication/Views/ViewCodecs.World.cs`, `ViewCodecs.Production.cs` (kinds 5–14) | N1 | Approved (a): `Net/WorldViewCodecTests.cs` | fast, ~800, D2 §6 |
| N6 | Player and detail views | `Net/Replication/Views/ViewCodecs.Player.cs`, `ViewCodecs.Detail.cs` (kinds 1–4, 15–25) | N1, S1 | Approved (a): `Net/PlayerViewCodecTests.cs` | fast, ~900, D2 §6 |
| N7 | Looks | `Net/Replication/Looks/*` | N1 | Build. Ask: [Slow] Rolled/Kit recipes rebuild the host's parts | strong, ~700, D2 §6 Looks |
| N8 | Events codec | `Net/Events/*` | N1 | Approved (a): `Net/EventCodecTests.cs` (every kind). Ask: spatial filter cases | fast, ~450, D2 §8 |
| N9 | Protocol and handshake | `Net/Session/{ProtocolCodec,Handshake,DataSetIdentity,PlayerColourRule}.cs` | N1, E1 | Approved (a): `Net/ProtocolCodecTests.cs` (every message incl. WorldReset/SessionRules/WeatherSync). Ask: `Net/HandshakeTests.cs` | fast, ~800, D3 §3, D4 §4 |
| U1 | UI plumbing and ownership filter | D5 WP-E files: `Meitou.Ui/MyGuiScreenHost.Keyboard.cs`, `Meitou.Engine/Input/InputBindings.cs`, `SettingsMenu.Descriptions.cs`, `UserConfig.cs`, create `Multiplayer/{NetUiContract,PlayerColours,JoinAddress}.cs`, `PlayerInterface.Net.cs` and stubs `PlayerInterface.{Multiplayer,Chat,Players,NetStatus,NetMenu}.cs`; hooks in `PlayerInterface.{Ui,Faithful,Frontend,Hud,cs,Picking,Portraits,Squads}.cs`, `GameHost.Interactive.cs` (Config, PlayerListHeld, F10 cheats gate); docs (`meitou-ui.md`, `game/ui-input.md`, `faithful-ui-parity.md`, `game/ui-screens.md`) | S1, N1, E1 | Quick run; `Ui/*` incl. `FaithfulInteractionTests`, `MyGuiScreenTests`, `UiStateTests`; single-player behaviour identical except the MULTIPLAYER entries. No new tests (ask for parser/colour unit tests) | strong, ~1100, D5 §0–§1, WP-E |
| G5 | Locked rule rows on clients | `SettingsMenu.cs`, `SettingsMenu.Drawing.cs` (`Entry.Locked`, `HostRules`) | N1 | `Ui/*` quick run; no new tests | fast, ~150, D4 §5 |

### Wave 3 (6 packages)

| Id | Title | Files | Depends | Acceptance | Tier, size, detail |
|---|---|---|---|---|---|
| N10 | Interest, delta, replicator, mirror | `Net/Replication/Delta/*` | N4–N7 | Approved (a)/(b): `Net/ReplicationTests.cs` (seeded drop/reorder/duplicate sink; after quiet the mirror equals the host's quantized view and views; removals, slot reuse, string loss, starvation recover; bytes logged). Ask: `Net/WireCoverageTests.cs` | strong, ~1400, D2 §7 |
| U2 | Title entry and Multiplayer window | `PlayerInterface.TitleMenu.cs`, `PlayerInterface.Multiplayer.cs` | U1 | `Ui/*`; screenshots of title/window both UI modes | fast, ~600, D5 WP-A |
| U3 | Chat | `PlayerInterface.Chat.cs`, `Multiplayer/ChatCommands.cs` | U1 | `Ui/*` | fast, ~500, D5 WP-B |
| U4 | Player list, name tags, map markers | `PlayerInterface.Players.cs`, `PlayerInterface.Stealth.cs`, `PlayerInterface.Map.cs` | U1 | `Ui/*`; single-player tags and map unchanged | fast, ~450, D5 WP-C |
| U5 | Status, Esc menu, players window, alerts | `PlayerInterface.NetStatus.cs`, `PlayerInterface.NetMenu.cs` | U1 | `Ui/FaithfulInteractionTests` (Esc → Exit → ButtonC with 7 rows) | fast, ~700, D5 WP-D |

### Wave 4 (2 packages)

| Id | Title | Files | Depends | Acceptance | Tier, size, detail |
|---|---|---|---|---|---|
| N11 | NetHost | `Net/Session/Host/{NetHost,HostPeer,HostEventTap,HostWorldLink,NetHostOptions,IHostGame}.cs`, `docs/net.md` (Host) | N2, N3, N8, N9, N10, S2–S5 | Build; quick run. Ask: `Net/EventTapTests.cs` (host consumers still get every event) | strong, ~1300, D3 §4 as amended |
| N12 | NetClient and jitter buffer | `Net/Session/Client/{NetClient,JitterBuffer,ClientEvents}.cs`, `docs/net.md` (Client) | N2, N3, N8, N9, N10, S4 | Build; quick run. Ask: `Net/JitterBufferTests.cs` | strong, ~1200, D3 §5 as amended |

### Wave 5 (2 packages)

| Id | Title | Files | Depends | Acceptance | Tier, size, detail |
|---|---|---|---|---|---|
| T1 | In-process host+client tests | `tests/Meitou.Tests/Net/SessionTests.cs` (+ a `SessionHarness` helper in the same folder) | N11, N12 | Approved (b)/(c): SyntheticTown host + replica over `LoopbackNetwork`/`ManualNetClock`; own MoveOrder moves the character, a foreign one is refused with the gate's reason; the client's newest snapshot equals the host's `PublishFor(n)` at that tick; trade/conversation/selection of one player invisible to the other. Ask: clock (pause by client, HostOnly refusal), reconnect keeps number and squads, join while paused | strong, ~600, D3 §9 |
| G1 | Game net plumbing | Create `GameHost.Network.cs`, `GameHost.News.cs` (WorldNews moved, forwarding, knockouts), `Multiplayer/NetSessionView.cs`; modify `GameHost.Interactive.cs` (two loop lines, request dispatch, F11 net line, window title, `new SettingsMenu(...) { HostRules = () => role == NetRole.Client ? netClient?.SessionRules : null }`); `docs/engine.md` (Multiplayer skeleton) | N11, N12, U1 | Full quick run; single-player frame identical (no net objects) | strong, ~700, D4 §1, §6 |

### Wave 6 (3 packages)

| Id | Title | Files | Depends | Acceptance | Tier, size, detail |
|---|---|---|---|---|---|
| G2 | Host mode | Create `GameHost.Hosting.cs` (StartHostingCore/StopHostingCore, `IHostGame`: Gate + Placeable, Rules, Weather); modify `GameHost.Saving.cs` (LocalPlayer, client refusal, saved message), `GameHost.NewGame.cs` (HostGame flows, main menu stops hosting), `GameHost.cs` (RecordingAppearances wrap), `Meitou.Rendering/WorldWeather.cs` (`Revision`); `docs/engine.md` (Host) | G1 | Full run; `QuickSaveTests`, `SaveWorldTests` | strong, ~700, D4 §2 |
| G3 | Client mode | Create `GameHost.Client.cs` (BeginJoinCore, replica build, Enter/Leave client mode, ClientFrameCore: relations row, creation, SetView); modify `GameHost.Transition.cs` (readiness `HasPresented`, settle tick only when Local), `GameHost.Editor.cs` (no RunTick on a replica), `GameHost.Blood.cs` (luckyren from SessionRules); `docs/engine.md` (Client) | G1, G5 | Full run | strong, ~900, D4 §3, D3 §7 |
| G4 | Command line and logs | `GameOptions.cs`, `Program.cs`, `GameLog.cs` | G1 | Quick run. Ask: option-combination and `JoinAddress` tests | fast, ~250, D4 §8–9 |

### Wave 7 (integration, no new package)

Two-instance smoke run (`--host --new-game` / `--join 127.0.0.1 --config meitou.guest.json --no-audio`), screenshots of every
window in both UI modes, a save/load while connected, kick and reconnect; tune `SendRate`/budget from logged bytes (R4).

## 5. Risks and open questions (with the recommended decision)

| # | Risk / question | Recommendation |
|---|---|---|
| R1 | LiteNetLib 2.x API differs from the specs' 1.3 names | N3 adapts in one file; if 2.1.4 lacks a needed feature, pin 1.3.5 and record why in `docs/net.md`. |
| R2 | The paused `JoinPlayer` path spawns characters outside a tick | Keep it (a joiner must not wait for an unpause); S5 runs the spawn under `Characters.BeginTick/EndTick` with Phase Inputs, as the paused path already does; T1 covers it once approved. |
| R3 | Entities skipped under budget pressure keep their last state, so interpolation stutters for them | v1 interpolates whole snapshots; the 256 KB/s budget sends every changed entity in the estimated scenes. Per-entity interpolation by `TickOf` is a later change if the smoke run shows stutter. |
| R4 | Bandwidth figures are estimates (D2 §9) | N10 tests and wave 7 log real bytes; adjust defaults then. |
| R5 | "Every look goes through `IAppearanceSource`" is Observed, not Verified | `AppearanceRecipe.FromLook` fallback; the [Slow] recipe test (ask) verifies it on the game data. |
| R6 | Client weather schedulers matching the host's is Observed | Resend `WeatherSync` every 60 s besides revision changes. |
| R7 | Host ticks outside `Advance` (settle, after creation) and their events | Allowed; their sounds/blood/floaters stay host-only (documented gap). |
| R8 | Host scene-load stages block `Poll` for ≥ 30 s | LiteNetLib's own threads keep peers alive (N3 confirms with 2.1.4); `DisconnectTimeout = 15000`; queued events are processed at the next `Poll`. |
| R9 | Player numbers are a byte | `NoSlot` when `NextNumber() > 255`. |
| R10 | Joiners do not see blood decals made before they joined (D4 `DecalsSync` dropped) | Accept for v1; add a `Decals` view later if wanted. |
| R11 | A `--join` boot builds title-world systems it then discards (D4's no-simulation boot deferred) | Accept; it only costs boot time. Revisit after wave 7. |
| R12 | Traffic is unencrypted; the password is only an HMAC proof | Accept for v1 (co-op among friends); state it in `docs/net.md`. |
| R13 | Many useful tests fall outside the approved categories (gate unit tests, failure cases, transport, handshake, jitter, tap, join, clock, reconnect) | Ask the user once, before wave 2, for one batch approval of the "ask" list; recommended: approve `CommandGateTests`, `PlayerJoinTests`, `EventTapTests` and the T1 clock/reconnect cases at minimum. |
| R14 | Single-player UI gains MULTIPLAYER on the title (Import slot) and in the Esc menu | Keep: the entry point is needed and the simulation is untouched; recorded in `docs/faithful-ui-parity.md`. |
| R15 | S1 exceeds the 1500-line guideline | Mandated single package (compile-error-driven rename); review it in two passes (renames, then semantics). |
| R16 | NPC-to-NPC relations on clients come from local data | Engine choice (only turret aim sounds read them); the player row is replicated. |
| R17 | Stale client view sends commands naming characters that just changed hands | The gate filters/refuses; refusals are rate-limited notices. |
| R18 | String tables and the look cache only grow during a connection | Bounded by `MaxStringTable` (then literals) and by the looks in play; accept. |
| R19 | Clients' `Tick` stamps on commands are presentation ticks | The host restamps every remote command (`Tick = world.Tick`); nothing on the client relies on them. |

## 6. Places a spec contradicted the fixed decisions (fixed here)

- D3 put snapshots on `DeliveryMethod.Unreliable` and acked whole fragmented frames: fixed to Sequenced with D2's acked
  per-entity baselines (C2, C3).
- D2 and D3 named LiteNetLib 1.3.1: the current stable is 2.1.4 (§1).
- D4 showed pause/speed notices only for other players' changes on the host: everyone now sees "<name> paused the game"
  (C42).
- D3 listed clock, reconnect, transport, handshake, jitter and tap tests as approved or proposed without approval: only the
  fixed categories are approved; the rest are marked **ask** (§4, R13).
- D1 forbade host ticks outside the session clock, which the fixed decisions do not require: allowed on the host (C37).

## 7. Notes from wave 1 (merged into `multiplayer` at 36b3418d; binding for later waves)

- **S1 as built.** `World.Actors` reads the issuer's selection with `Find` (never creates a session). Extra helpers exist:
  `World.PostOwnerOfSlot(slot, text)` (internal), `World.HolderOf` + holder list (S2 registers its systems as `INpcHolder`).
  `PlayerSystem.ContainerFor`, `ProductionSystem.Near/Queue`, `ArrestSystem.StartCaging` take a player parameter.
  `DialogueSystem.Recruit` still uses `Player.Local` and `NewSquad(owner 0)`: **S2 fixes it**. `WorldPublish.PublishSquads`
  still lists every owner's squads: **S4 fixes it**. The joiner engine choices (no money added, head cap skipped) are
  documented by **S5** under "Joining". `TakeMessages()` returns `PlayerMessage`; tests use `.Select(m => m.Text)`.
- **N1 as built.** `ViewState` lives in `src/Meitou.Net/Replication/Views/ViewState.cs` (namespace `Meitou.Net.Replication`):
  **N8 uses it, N10 must not redefine it** (N10 adds only `ClientFrame`). `NetWriter.WriteEnum<T>` mirrors `ReadEnum<T>`
  (byte/sbyte raw; unsigned wider as varint; signed wider as zigzag varint). `KeyedViewSync` puts **slot numbers, not keys**,
  on the wire (flags byte, order as count+slots, upserts as count+(slot,item)); **N5/N6 decode with N1's `KeyedViewDecoder`**
  and keep keys unique (duplicates throw). Extra limits: `NetLimits.MaxViewItems`, `MaxViewBody` (soft), `MaxReliableMessage`.
  CommonCodec has Vector4, NullableInt, NullableSingle helpers.
- **Writer-side limits.** Readers refuse over-limit data and a refusal on a reliable channel drops the peer, so **every codec
  must clamp or validate on WRITE** (string byte lengths, counts) so a reader never refuses what a writer produced.
- **Smallest-three precision** is ≈0.005 rad worst case (measured 0.0045): tests assert ≤ 0.005, not the spec's 0.003; yaw uses Yaw16.
- **Known failures not ours:** quick run baseline `MyGuiScreenTests.ListsScrollSelectAndClipAndTabsHideInactiveContents`;
  full run with the game install also fails on base 8f26eca7: `TextureLoaderTests.Loads_base_game_png_tga_and_jpg`,
  `ItemIconRenderTests.Icons_drawn_from_the_mesh...`, `TradeUiTests.Scrolled_stock_keeps_its_cells_and_buys_the_visible_item` (both modes).
- **Test moves.** The "join while paused" case moves from S5 to **T1** (it needs S4's paused filter and S5's `ApplyJoins` together).
  Wave-1 "Ask" tests go to **T0** (wave 2).
