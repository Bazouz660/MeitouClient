# D3 Net sessions: host, client, transport, clock (implementation spec)

Scope: new project `src/Meitou.Net` plus hooks in `Meitou.Engine`, one in `Meitou.Simulation` (`World.Present`) and small edits in `Meitou.Game`.
Fixed: host-authoritative listen server, LiteNetLib UDP, co-op, player 0 = host, clients never tick.

## 0. Invariants (all packages)

- **Single-player unchanged.** Without `NetHost`/`NetClient`: `ClockAuthority == null`, `Mode == Local`, no event subscribers; every new branch gates on these. No hashed state is added; `GoldenHashTests` pass untouched.
- **One thread.** All on the frame thread; the transport delivers only inside `Poll` (LiteNetLib `UnsyncedEvents = false`). Pump points: `BeginFrame` after `window.DoEvents()`, before `session.Advance` (`GameHost.Interactive.cs:422`); `EndFrame` after `StepSceneLoad()` (`:429`). Between them only `session.Advance` ticks the world; per-tick hook `WorldSession.SimulationTicked`.
- **Host stamping.** Remote commands pass `CommandGate` (D1) and are enqueued with `Tick = world.Tick` at receipt; a stale stamp slides forward (`Commands.cs:12`); while paused the paused-interface path takes them.
- **Bounded input.** Readers bound counts/lengths and throw `InvalidDataException`; the host drops such a peer (`ProtocolError`), never crashes. Little-endian, raw IEEE floats, UTF-8, no culture formatting. Doc comments cite `docs/net.md`; warnings are errors.

## 1. Work packages and file ownership

| WP | Content | Files (owned exclusively) | Needs |
|---|---|---|---|
| 1 | transport, wire, loopback, clock | `src/Meitou.Net/Meitou.Net.csproj`, `Wire/NetWriter.cs`, `Wire/NetReader.cs`, `Transport/INetTransport.cs`, `Transport/LiteNetTransport.cs`, `Transport/LoopbackNetwork.cs`, `NetClock.cs`; `MeitouClient.slnx`; `tests/Meitou.Tests/Meitou.Tests.csproj` (ProjectReference); `THIRD_PARTY_NOTICES.md` | none |
| 2 | protocol, handshake, registry | `Protocol/NetProtocol.cs`, `Protocol/Messages.cs`, `Protocol/ClockState.cs`, `Protocol/DataSetIdentity.cs`, `Protocol/Handshake.cs`, `Protocol/PlayerRegistry.cs`, `Protocol/WeatherCodec.cs` | WP1 signatures |
| 5 | engine hooks | `Meitou.Engine/WorldSession.cs`, `Meitou.Engine/Time/ClockRequest.cs` (new), `Meitou.Simulation/World.cs`, `World.Loading.cs`, `World.Interface.cs`; Game: `PlayerInterface.Hud.cs`, `PlayerInterface.Faithful.cs`, `PlayerInterface.Floaters.cs`, `GameHost.Characters.cs` | none |
| 3 | host loop | `Host/NetHost.cs`, `Host/HostPeer.cs`, `Host/SnapshotSender.cs`, `Host/HostEventTap.cs`, `Host/EventCodec.cs`, `Host/HostWorldLink.cs`, `Host/NetHostOptions.cs` | WP1, WP2, WP5, D1, D2, sim sessions |
| 4 | client loop, jitter buffer | `Client/NetClient.cs`, `Client/SnapshotAssembler.cs`, `Client/JitterBuffer.cs`, `Client/ClientEvents.cs`, `Client/ClientWorldLink.cs` | WP1, WP2, WP5, D1, D2; WP3's `EventCodec` signature |
| 6 | game integration | `GameHost.Net.cs` (new); edits in `GameHost.Interactive.cs`, `GameHost.Transition.cs`, `GameHost.Editor.cs`, `GameHost.Saving.cs`, `GameHost.NewGame.cs`, `GameHost.Characters.cs` (`SendFocus` only), `UserConfig.cs`, `GameOptions.cs`, `Meitou.Game.csproj` | all |

**Order.** A: WP1, WP2, WP5 in parallel (disjoint files; WP2 codes against WP1's signatures). B: WP3 and WP4 in parallel once A and the D1/D2 core APIs landed (shared only: `EventCodec`, written by WP3, signature in §4). C: WP6, then docs (§9).

## 2. WP1: transport, wire, loopback

`Meitou.Net.csproj` copies `Meitou.Simulation.csproj`: refs Core, Data, Simulation, Engine; `InternalsVisibleTo Meitou.Tests`; `<PackageReference Include="LiteNetLib" Version="1.3.1" />` (pin the latest stable exactly; confirm on nuget.org). Add the row `| LiteNetLib 1.3.1 | src/Meitou.Net (UDP transport) | MIT |` to `THIRD_PARTY_NOTICES.md` and the project under `/src/` in the slnx.

### Wire primitives (exposed to D1/D2)

```csharp
namespace Meitou.Net.Wire;
public sealed class NetWriter
{
    public NetWriter(int capacity = 256);
    public int Length { get; }
    public ReadOnlySpan<byte> Span { get; }
    public void Clear();
    public void U8(byte v); public void U16(ushort v); public void U32(uint v); public void U64(ulong v);
    public void I32(int v); public void I64(long v); public void F32(float v); public void F64(double v); public void Bool(bool v);
    public void VarU(ulong v);              // LEB128
    public void VarI(long v);               // zigzag LEB128
    public void Str(string? v);             // VarU(byteLength + 1), 0 = null
    public void Bytes(ReadOnlySpan<byte> v); // VarU(length) + bytes
    public void Raw(ReadOnlySpan<byte> v);
    public void Vec3(Vector3 v); public void Quat(Quaternion v); public void Guid(System.Guid v);
}
public sealed class NetReader
{
    public NetReader(ReadOnlyMemory<byte> data);
    public int Remaining { get; }
    // Mirrors of every writer method (U8() ... Guid()), plus:
    public string? Str(int maxBytes = 4096);
    public byte[] Bytes(int maxBytes);
    public int Count(int max);              // VarU bounded by max
    // Overrun, a malformed varint or a limit breach throws InvalidDataException.
}
```

### Transport interfaces

```csharp
namespace Meitou.Net.Transport;
public readonly record struct NetConnectionId(int Value);
public enum NetChannel : byte { Snapshots = 0, Control = 1, Bulk = 2, Events = 3 }
public enum NetDropReason : byte { Closed, Timeout, Rejected, Error }
public readonly record struct NetLinkStats(int RoundTripMs, float LossPercent, long BytesSent, long BytesReceived);
public interface INetListener
{
    byte[]? Request(string address, ReadOnlySpan<byte> connectData); // server: null accepts, else reject payload
    void Connected(NetConnectionId peer);
    void Received(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data);
    void Dropped(NetConnectionId peer, NetDropReason reason, ReadOnlySpan<byte> data); // data: the Reject payload, if any
}
public interface INetTransport : IDisposable
{
    void Poll(INetListener listener);
    void Send(NetConnectionId peer, NetChannel channel, ReadOnlySpan<byte> data);
    void Drop(NetConnectionId peer, ReadOnlySpan<byte> payload);
    int MaxUnreliablePayload(NetConnectionId peer);
    NetLinkStats Stats(NetConnectionId peer);
    string Address(NetConnectionId peer);
}
public interface INetServerTransport : INetTransport { void Listen(int port); }
public interface INetClientTransport : INetTransport { NetConnectionId Connect(string host, int port, ReadOnlySpan<byte> connectData); }
```

`Snapshots` is unreliable/unordered (D3 sequences, fragments and acks it; payload ≤ `MaxUnreliablePayload`). `Control`, `Bulk`, `Events` are reliable-ordered and transport-fragmented. `Control` carries handshake, commands, clock, chat, roster and messages, so a client's commands and clock requests keep their order.

**`LiteNetTransport`** (server and client constructors) maps: `NetManager(listener) { ChannelsCount = 4, UnsyncedEvents = false, AutoRecycle = true, EnableStatistics = true, DisconnectTimeout = 10000, IPv6Enabled = true }`; `Request` = `ConnectionRequestEvent` (copy `request.Data`, `Accept()` / `Reject(bytes)`); Snapshots = `DeliveryMethod.Unreliable`, others `ReliableOrdered`; stats = `peer.RoundTripTime`, `peer.Statistics.PacketLossPercent/BytesSent/BytesReceived`; `MaxUnreliablePayload` = `peer.GetMaxSinglePacketSize(Unreliable)`; `Drop` = `peer.Disconnect(bytes)`; `Dropped` from `PeerDisconnectedEvent` with `DisconnectInfo.AdditionalData`. Debug-assert callbacks arrive on the `Poll` thread. Names as of 1.3; a different pinned version changes only this file.

**`LoopbackNetwork`** (tests; deterministic):

```csharp
public sealed record LoopbackOptions { public int LatencyMs { get; init; } public int JitterMs { get; init; } public float UnreliableLoss { get; init; } public int Mtu { get; init; } = 1200; public int Seed { get; init; } = 1; }
public sealed class LoopbackNetwork(LoopbackOptions options, ManualNetClock clock)
{
    public INetServerTransport Server();   // one per network
    public INetClientTransport Client();
    public void Sever(NetConnectionId clientSide);   // both ends get Dropped(Timeout) at their next Poll
}
```

Each send: `deliverAt = now + latency + random(0..jitter)` (`System.Random(Seed)` in send order). Reliable channels never lose and keep order (`deliverAt = max(deliverAt, last on that link+channel)`); unreliable sends drop with `UnreliableLoss` and may reorder. `Poll` delivers everything due in `(deliverAt, sendSequence)` order. An unreliable payload over `Mtu` throws (catches fragmenter bugs).

**Clocks** (`NetClock.cs`): `public interface INetClock { long NowMicroseconds { get; } }`, implemented by `StopwatchNetClock` and by `ManualNetClock { NowMicroseconds { get; set; } Advance(TimeSpan) }`.

## 3. WP2: protocol, handshake, registry

```csharp
namespace Meitou.Net.Protocol;
public static class NetProtocol
{
    public const ushort Version = 1;   // bump on ANY wire change, including D1/D2 codecs
    public const int DefaultPort = 40120, MaxPlayers = 16, MaxNameLength = 24, MaxChatLength = 200, MaxReliableMessage = 1 << 20;
    public static ReadOnlySpan<byte> Magic => "MEITOU"u8;   // the connect request: Magic + U16 Version
}
public enum NetMessage : byte { Challenge = 1, Hello, Welcome, Reject, Ready, WorldReset, Commands, CommandResult, ClockRequest, ClockState,
    Chat, ChatLine, Roster, View, Message, Weather, Snapshot = 0x20, Ack = 0x21, Bulk = 0x30, Events = 0x40 }
public enum RejectCode : byte { Protocol, Build, Data, Password, Full, Banned, Duplicate, Kicked, HostClosed, Timeout, ProtocolError, NoSlot, LoadTimeout }
```

### Message records

Each message has `void Write(NetWriter w)`, which writes the `NetMessage` byte first, and `static T Read(NetReader r)`.

```csharp
public sealed record Challenge(byte[] Nonce /*16*/, string HostName, bool NeedsPassword);
public sealed record Hello(ushort Protocol, string Build, Guid Player, string Name, byte[] Proof /*0 or 32*/, DataSetIdentity Data);
public sealed record NetRules(int SendRate, ClockPolicy ClockPolicy, int MaxPlayers);
public sealed record Welcome(byte Player, ushort Epoch, Vector3 Focus, ClockState Clock, NetRules Rules, IReadOnlyList<RosterEntry> Roster);
public sealed record Reject(RejectCode Code, string Reason);          // also the Drop payload
public sealed record Ready(ushort Epoch);
public sealed record WorldReset(ushort Epoch, byte Player, Vector3 Focus, ClockState Clock, bool IsJoin);  // IsJoin: made from the Welcome on the client
public sealed record CommandResult(uint Sequence, string Reason);
public sealed record ChatLine(byte Player, string Name, string Text);
public enum NetPlayerState : byte { Host, Loading, Playing, Away }
public sealed record RosterEntry(byte Player, string Name, NetPlayerState State, ushort RoundTripMs);
public sealed record ViewUpdate(Vector3 Target, float Distance);
public sealed record ServerMessage(string Text, bool Error);
public sealed record WeatherSync(WeatherTime Time, IReadOnlyList<WeatherRegionSnapshot> Regions); // WeatherCodec: every field in declaration order, nested WindInterpolation/RegionFog too
public enum ClockPolicy : byte { AnyPlayer, HostOnly }
```

Inline layouts (WP3/WP4): `Commands` = `U16 epoch, U32 firstSeq, Count(n ≤ 256), n × D1 command`; `ClockRequest` = `U8 kind, F64 speed`; `Chat` = `Str text`; `Snapshot`, `Ack`, `Bulk`, `Events` in §4/§5.

### Clock state

```csharp
public enum ClockChangeKind : byte { None, Paused, Resumed, Speed, TimeSet }
public readonly record struct ClockChange(int Player /* -1 none */, ClockChangeKind Kind, double Speed);
public readonly record struct ClockState(ushort Epoch, double Speed, double LastSpeed, bool HostBusy, double AchievedSpeed,
    long AnchorTick, double AnchorHours, ClockChange Change);
```

### Data set

```csharp
public sealed record DataSetEntry(string Name, ulong Hash, long Length);
public sealed record DataSetIdentity(IReadOnlyList<DataSetEntry> Files)
{
    public ulong Combined { get; }      // first 8 bytes of SHA-256 over (Name, Hash, Length) in load order
    public static DataSetIdentity Compute(LoadOrder order);   // per file: first 8 bytes of SHA-256 of the content; caller's thread
    public static string? Difference(DataSetIdentity host, DataSetIdentity client); // null when equal
}
public sealed record NetIdentity(Guid PlayerId, string Name, string Build, DataSetIdentity Data);   // who this process is (host or client)
```

`Compute` takes `LoadOrder.FromInstall(install)`, what the game loads (`Program.cs:64`, `WorldFrame.cs:493`); content hashes (not names or timestamps) catch mod versions. `Difference` reads `"Missing: A.mod, B.mod; extra: C.mod; different version: gamedata.base (and 3 more)."` (≤ 5 names per list), or `"Same mods in a different load order."`.

### Handshake (`Handshake.cs`, pure functions the host and client both call)

```csharp
public static byte[] Proof(string? password, ReadOnlySpan<byte> nonce, Guid player); // [] when no password; else HMACSHA256(SHA256(UTF8(password)), nonce ‖ player.ToByteArray())
public static Reject? CheckRequest(ReadOnlySpan<byte> connectData, int connected, int maxPlayers, bool addressBanned);
public static Reject? CheckHello(Hello hello, NetIdentity host, string? password, ReadOnlySpan<byte> nonce, bool allowBuildMismatch, bool banned);
public static string SanitizeName(string raw, int number);   // trim, drop control chars, clamp 24; empty becomes "Player {n}"
```

Check order and exact reasons: Protocol (connect magic/version) `Incompatible network protocol (yours {c}, host's {h}). Update Meitou.`; Full `The game is full ({n} players).`; Banned (address, then GUID) `You are banned from this game.`; Build `Version mismatch: you run {c}, the host runs {h}.`; Data `Your game data differs from the host's. {Difference}`; Password `This game needs a password.` / `Wrong password.` (`FixedTimeEquals`); NoSlot `No free player slot.`. Others: Kicked `Kicked by the host: {reason}`; HostClosed `The host ended the game.`; Timeout `Lost connection to the host.`; LoadTimeout `Took too long to load.`; Duplicate (to the older connection) `Signed in from another place.`.

### Player registry

```csharp
public sealed class PlayerRegistry
{
    public void Seed(IEnumerable<KnownPlayer> known);            // a world's list (save module); called at start and after each world swap
    public int Assign(Guid id, string name, out bool isNew);     // -1: none free
    public bool TryNumber(Guid id, out int number);
    public bool TryId(int number, out Guid id);
    public IReadOnlyList<KnownPlayer> Known { get; }
}
```

Rules: 0 is always the current host, never given to a remote GUID (even one the save lists as 0). Lookup: session map, then seeded list, then the lowest number in 1..255 held by neither. After a world swap the save's list wins; a connected player whose number it gives to someone else gets the lowest free one, told by `WorldReset.Player`. `MaxPlayers` limits connected players, not known ones.

## 4. WP3: host loop

```csharp
namespace Meitou.Net.Host;
public sealed record NetHostOptions
{
    public int Port { get; init; } = NetProtocol.DefaultPort;
    public int MaxPlayers { get; init; } = 4;            // connected, host included, 2..16
    public string? Password { get; init; }
    public string HostName { get; init; } = "Meitou";
    public ClockPolicy ClockPolicy { get; init; } = ClockPolicy.AnyPlayer;
    public int SendRate { get; init; } = 20;             // 10..30 per second while running
    public int PausedSendRate { get; init; } = 5;
    public int MaxBytesPerSecond { get; init; } = 256 * 1024;   // per client
    public float EventRadius { get; init; } = 10000;     // world units round a client's view (engine choice)
    public bool AllowBuildMismatch { get; init; }
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(180);
}
public readonly record struct NetPeerStats(int RoundTripMs, float LossPercent, int BytesInPerSecond, int BytesOutPerSecond, int LastSnapshotBytes);
public sealed record NetPlayerInfo(int Number, Guid Id, string Name, NetPlayerState State, NetPeerStats Stats);

public sealed class NetHost : IClockAuthority, IDisposable
{
    public NetHost(WorldSession session, INetServerTransport transport, NetHostOptions options, NetIdentity self, INetClock? clock = null);
    public void Start();                         // Listen; Seed registry from HostWorldLink.KnownPlayers(world); session.ClockAuthority = this; Simulation.RequestedPause = false; tap.Attach(session)
    public void BeginFrame();
    public void EndFrame(bool hostBusy);         // hostBusy: the host is under its loading screen (GameHost.Covered)
    public ClockPolicy ClockPolicy { get; set; }
    public Func<WeatherSync?>? WeatherSource { get; set; }
    public IReadOnlyList<NetPlayerInfo> Players { get; }   // host first
    public Vector3? ViewOf(int player);                     // last ViewUpdate (D2 interest culling)
    public void PostMessage(int player, string text, bool error = false);   // player -1: every remote player
    public void SendChat(string text);
    public void Kick(int player, string reason);
    public void Ban(int player, string reason, bool byAddress = false);    // bans last for the session
    public IReadOnlyCollection<Guid> Bans { get; }
    public event Action<NetPlayerInfo>? PlayerJoined, PlayerLeft;
    public event Action<ChatLine>? Chat;
    public event Action<ClockChange, string>? ClockChanged;   // with the player's name
    void IClockAuthority.Request(ClockRequest request);       // the host's own input, as player 0
    public void Dispose();                       // Drop every peer with HostClosed; session.ClockAuthority = null
}
```

`HostPeer`: connection id, state (`Challenged`, `Loading`, `Playing`, `Away`), nonce, number, GUID, name, address, view; encoder (D2), `SnapshotSender`, `NextSendUs`, `LastSentTick`, `LastSentSnapshot`; command and chat buckets; byte counters.

**`BeginFrame()`**: `tap.BeginFrame(world)`, then `transport.Poll`:
- **Request**: `Handshake.CheckRequest`; a rejection returns `Reject` bytes. **Connected**: peer `Challenged` with a `RandomNumberGenerator` nonce; send `Challenge`.
- **`Hello`**: `CheckHello` (reject: drop with it); `Assign` (-1: `NoSlot`); a live peer with the same GUID is dropped with `Duplicate`; `HostWorldLink.EnsureSession(world, n)`, `HostWorldLink.Remember(world, new KnownPlayer(id, n, name))`; encoder = `HostWorldLink.CreateEncoder(n)`; state `Loading`; send `Welcome(n, epoch, FocusOf(world, n) ?? session.Camera.Current.Target, clockState, rules, roster)`, then `WeatherSync` if a source exists; broadcast roster; raise `PlayerJoined`.
- **`Ready(e)`**, `e == epoch`: `Playing`; `encoder.Reset()`, `sender.Reset()` (next snapshot full, all bulk views); `NextSendUs = now`.
- **`Commands`**: only when `Playing` and the epoch matches. One token per command (200/s, burst 400; excess dropped, ≤ 1 `ServerMessage`/s). Per command: `cmd = CommandCodec.Read(r)`; `admitted = CommandGate.Admit(world, n, cmd, out reason)`; null → `CommandResult(seq, reason ?? "Not allowed.")` (≤ 2/s); else `world.Commands.Enqueue(admitted with { Tick = world.Tick })`.
- **`ClockRequest`** → `ApplyClock(n, r)`. **`Chat`**: strip control characters, ≤ 200 chars, 5 lines per 5 s; broadcast `ChatLine`, raise `Chat`. **`View`** → `peer.View`. **`Ack(epoch, seq)`** (Snapshots channel), epoch matching → `sender.Ack(seq)`.
- **Dropped**: a `Loading`/`Playing` peer becomes `Away` (number and squads kept): `HostWorldLink.PlayerLeft(world, n)`, roster, `PlayerLeft`. A `Challenged` one is forgotten. Any `InvalidDataException` drops with `ProtocolError`.
- Then timeouts: `Challenged` past `HelloTimeout` → `Timeout`; `Loading` past `LoadTimeout` → `LoadTimeout`.

**`ApplyClock(player, r)`**: `HostOnly` and `player != 0` → `ServerMessage("Only the host can change the game speed.")`. Else record `(Speed, LastNonZeroSpeed)`, `r.ApplyTo(session.Simulation)`; unchanged → return; kind = to 0 `Paused`, from 0 `Resumed`, else `Speed`; `change = new ClockChange(player, kind, speed)`, clock dirty, raise `ClockChanged(change, name)`. `RequestedPause` stays false while hosting (`ModalsPause`, §6).

**`EndFrame(hostBusy)`**:
1. `tap.EndFrame(world)`.
2. Clock: `expected = anchorHours + (world.Tick − anchorTick) · world.TickSeconds / GameClock.SecondsPerGameHour`; `|session.Clock.TotalHours − expected| > 1e-6` → change `TimeSet` (load, time slider, new game). Broadcast `ClockState(epoch, Speed, LastNonZeroSpeed, hostBusy, Simulation.AchievedSpeed, world.Tick, Clock.TotalHours, change)` when dirty, when `hostBusy` changed, else every 2 s (change `None`, player -1); re-anchor.
3. Events: per `Playing` peer, `EventCodec.Write(w, epoch, tap.Batch, p => peer.View is not { } v || Vector3.DistanceSquared(p, v) <= R²)` on Events unless empty; `tap.ClearBatch()`.
4. Snapshots, per `Playing` peer when `now ≥ NextSendUs`, `sender.Budget > 0`, and (`world.Tick != LastSentTick` or `world.Snapshot != LastSentSnapshot` (paused-interface republish) or 1 s passed): `snap = HostWorldLink.PublishFor(world, n)`; `frame = encoder.Capture(snap)`; `(baseSeq, baseFrame) = sender.Baseline()`; write `I64 snap.Tick, I64 hostTimeUs`, then `encoder.WriteDelta(w, baseFrame, frame)`; `sender.Send(...)`; `sender.Remember(seq++, frame)`; `encoder.CollectBulk(snap, bulk)` → each as `Bulk` (`U16 epoch, U16 kind, I64 revision, Bytes payload`) on Bulk; `NextSendUs += 1e6 / (Simulation.IsPaused || hostBusy ? PausedSendRate : SendRate)`, reset to `now + interval` when over one interval behind.
5. `PostMessage` queue → `ServerMessage` (player 0 ignored: the host shows its own notices). Roster on change and every 2 s; stats once a second.

**`SnapshotSender`** (per peer): ring of 64 `(Seq, Epoch, Frame)`; `Ack(seq)` keeps the highest acked of this epoch; `Baseline()` returns it, `(0, null)` once it left the ring. Fragments: ⌈len / (MaxUnreliablePayload − 13)⌉ ≤ 255 packets `[U8 Snapshot][U16 epoch][U32 seq][U32 baseSeq][U8 index][U8 count][slice]`; more breaks D2's size contract (drop the peer with `ProtocolError`, log once). Budget: token bucket refilled at `MaxBytesPerSecond`; a send spends its size and may go negative once. `Reset()` clears ring and ack.

### `HostEventTap` (fan-out without stealing from the host's own consumers)

```csharp
sealed class HostEventTap
{
    public void Attach(WorldSession s);      // s.SimulationTicked += Collect; s.WorldReplaced += _ => Reset()
    public void BeginFrame(World w);         // per queue: if w.X is null, w.X = own queue; remember (ref, mark = ref.Count)
    public void EndFrame(World w);           // Collect (paused-path events at w.Tick); if ref is own: own.Clear(), mark = 0
    public IReadOnlyList<TickEvents> Batch { get; }
    public void ClearBatch();
}
public sealed record TickEvents(long Tick, SoundEvent[] Sounds, BloodMark[] Blood, Floater[] Floaters);
```

X = `Sounds`, `BloodMarks`, `Floaters`. `Collect(w)`: if `w.X` is no longer `ref`, re-mark; else the new items are the queue enumerated from `mark` (FIFO snapshot), then `mark = ref.Count`; append under `w.Tick`. Correct because between `BeginFrame` and `EndFrame` nothing dequeues them (the host drains while drawing, after `EndFrame`: `UpdateAudio` `Interactive.cs:449`, `UpdateBlood`, `DrawFloaters`), ticks only enqueue, and host consumers assign their own queues, never adopt one (WP5 fixes `PlayerInterface.Floaters`). A host with audio, blood or floaters off still forwards through `own`.

**`EventCodec`** (WP3 writes it; WP4 calls it):

```csharp
public static class EventCodec
{
    public static void Write(NetWriter w, ushort epoch, IReadOnlyList<TickEvents> batch, Func<Vector3, bool> relevant);
    public static (ushort Epoch, List<TickEvents> Batch) Read(NetReader r);   // ≤ 4096 items per message
}
```

Encodings: `CharacterId` = `VarI slot, VarU generation`. Batch = `U16 epoch, Count ticks`, per tick `VarU tickDelta, Count` each list. `SoundEvent` = `U8 kind, id Subject, id Other, Vec3, U8 flags(Heavy|Unarmed|Robot|Armoured|Fatal), Str Item`. `BloodMark` = `U8 kind, id, Vec3, F32×2 dir, F32 amount, U32 colour, Str race`. `Floater` = `U8 kind, id, Vec3, U8 flags(TargetIsPlayer|SourceIsPlayer), Str part, F32×5 HitDamage, F32×3 ArmourAbsorbed, F32 healed`.

**`HostWorldLink`** (internal static): the only caller of other modules' world APIs (§8), so a mismatch is fixed in one file. `PlayerLeft(world, n)` enqueues `CloseTrade`, `EndConversation`, `CloseContainer` with `{ Tick = world.Tick, Player = n }`; squads stay owned and finish their orders.

**World swap** (`session.WorldReplaced`: load, new game, main menu): `epoch++`; tap reset; `registry.Seed(KnownPlayers(newWorld))`; per connected peer: re-assign its number, `EnsureSession(newWorld, n)` (tick 0, phase `Outside`), `encoder.Reset()`, `sender.Reset()`, state `Loading`, send `WorldReset(epoch, n, FocusOf(newWorld, n) ?? camera target, clock, IsJoin: false)`. Old-epoch commands are ignored.

## 5. WP4: client loop and jitter buffer

```csharp
namespace Meitou.Net.Client;
public enum NetClientState : byte { Idle, Connecting, Authenticating, Joined, Playing, Closed }
public sealed record NetDisconnect(RejectCode Code, string Reason);
public readonly record struct NetClientStats(int RoundTripMs, float LossPercent, int BytesInPerSecond, int BytesOutPerSecond,
    int DelayMs, int Starved, int MissingBaseline, float SnapshotsPerSecond);

public sealed class NetClient : IClockAuthority, IDisposable
{
    public NetClient(INetClientTransport transport, NetIdentity self, IViewerDecoder decoder, INetClock? clock = null);
    public NetClientState State { get; }
    public int Player { get; }  public ushort Epoch { get; }  public NetRules? Rules { get; }
    public NetDisconnect? Closed { get; }
    public IReadOnlyList<RosterEntry> Roster { get; }
    public NetClientStats Stats { get; }
    public bool HasPresented { get; }            // a snapshot of this epoch is on show
    public void Connect(string host, int port, string? password);
    public void Attach(WorldSession replica);    // its Mode must be Replica; sets replica.ClockAuthority = this
    public void Ready();                         // sends Ready(Epoch); State = Playing
    public void BeginFrame(double realSeconds);  // Poll, then present into the attached session
    public void EndFrame();                      // uplink
    public void SendChat(string text);
    public void Leave();                         // Drop(Closed); State = Closed
    void IClockAuthority.Request(ClockRequest r);   // sends ClockRequest (Control)
    public event Action<WorldReset>? WorldReset; // the join (IsJoin) and every later swap: the game builds a replica, then Attach + Ready
    public event Action<NetDisconnect>? Disconnected;
    public event Action<ChatLine>? Chat;
    public event Action<ClockChange, string>? ClockChanged;
    public event Action<string, bool>? Message;  // ServerMessage, CommandResult (error)
    public event Action<WeatherSync>? Weather;
    public event Action? RosterChanged;
}
```

**Handshake**: `transport.Connect(host, port, Magic + Version)` (`Connecting`). `Challenge`: needs a password and none given → close `Password, "This game needs a password."`; else send `Hello(Version, Build, PlayerId, Name, Proof(...), Data)` (`Authenticating`). `Welcome`: `Player`, `Epoch`, `Rules`; `Joined`; raise `WorldReset(new(epoch, player, focus, clock, IsJoin: true))`. A drop: its `Reject` payload, else `Timeout, "Lost connection to the host."`; `Closed`, `Disconnected`.

**Receiving**:
- `Snapshot` fragments → `SnapshotAssembler.Add(epoch, seq, baseSeq, index, count, slice)` (≤ 8 partials, dropped after 1 s; seqs ≤ the newest completed ignored). A completed message of this epoch: read `I64 tick, I64 hostTimeUs`; `baseline = baseSeq == 0 ? null : ring[baseSeq]` (missing → `MissingBaseline++`, drop); `frame = decoder.ReadDelta(r, baseline)`; `ring[seq] = frame` (64); `jitter.Add(tick, hostTimeUs, nowUs, frame)`; send `Ack` (`U8 Ack, U16 epoch, U32 newestSeq`) at once on Snapshots.
- `Bulk` (this epoch) → `decoder.ApplyBulk(...)`. `Events` (this epoch) → `ClientEvents` (≤ 4096 pending, oldest dropped). `ClockState` → stored and applied (below); a change with player ≥ 0 raises `ClockChanged(change, roster name)`.
- `WorldReset`: clear ring, assembler, jitter, events; `decoder.Reset()`; set `Epoch`, `Player`; `Joined`; `HasPresented = false`; detach; raise `WorldReset`.
- `Roster`, `ChatLine`, `ServerMessage`, `CommandResult`, `WeatherSync` raise their events. `InvalidDataException` or a decoder exception → `Leave` with `ProtocolError, "Bad data from the host."`.

### `JitterBuffer` (presentation clock in host ticks)

The clock runs in host ticks, not host time. Snapshots at 20 Hz land 1 or 2 ticks apart at speed 1, and spacing them by arrival time would judder.

```csharp
sealed class JitterBuffer
{
    public void Add(long tick, long hostTimeUs, long arrivalUs, ReplicationFrame frame);   // same tick: keep the newer
    public void SetRate(double ticksPerSecond, int sendRate);   // 0 while the host is paused or busy; clears the arrival samples
    public Presentation? Advance(long nowUs, double realSeconds);
    public void Clear();
    public double DelaySeconds { get; }
    public int Starved { get; }
}
public readonly record struct Presentation(BufferedFrame Older, BufferedFrame Newer, float Alpha, float MotionAlpha, double Tick);
```

Constants (engine choices, in docs/net.md): start delay 0.10 s, `MinDelay` 0.05 s, `MaxDelay` 0.5 s, `ExtrapolationCap` 0.1 s, `SnapThreshold` 0.5 s, correction ±10 %, sample window 1 s.

`Advance(now, dt)`, rate `r`:
1. Live estimate `L = max over window samples of (tick_i + (now − arrival_i)/1e6 · r)` (follows the least-delayed packet); `r == 0` → `L = newestTick`. `target = L − delay · r`.
2. `P` unset or `|target − P| > SnapThreshold · max(r, 30)` → `P = target`. Else `r > 0` → `P += dt · r · (1 + clamp(0.5 · (target − P)/r, −0.1, 0.1))`; paused → `P = min(P + dt · lastRunningRate, newestTick)` (buffered motion finishes, then holds). Then `P = min(P, newestTick + ExtrapolationCap · r)`.
3. Pair: `S0` = last frame with tick ≤ P, `S1` = first with tick > P → `Alpha = MotionAlpha = (P − T0)/(T1 − T0)`. Starved (no `S1`): newest and the one before, `Alpha = 1`, `MotionAlpha = 1 + (P − Tn)/(Tn − Tn−1)` (≤ 2); `Starved++` and `delay += 0.02 s` once per episode. No `S0`: `(oldest, oldest, 0, 0)`.
4. Adaptive delay: per arrival lateness `ℓ = max(0, (L_at_arrival − tick)/r)`, decaying peak `J = max(ℓ, J · exp(−dt/2 s))`; `targetDelay = clamp(1.25/sendRate + J + 0.01, MinDelay, MaxDelay)`; `delay` rises at once, falls ≤ 0.005 s per real second.
5. Frames older than `S0` are dropped (the decode ring keeps its own 64). `BufferedFrame` caches `decoder.Restore(frame)` once, so the renderer's reference-keyed `IndexPrevious` does not re-index every frame.

**Presenting** (`BeginFrame`, `Playing` and attached):
1. `p = jitter.Advance(...)`; null → nothing presented.
2. Teleport snap: a character with `|pos1 − pos0| > 500 · ΔT/30 + 20` units (≈110 mph of game time) gets the newer entry in an `older'` copy (`with { Characters = ... }`), cached per `(S0, S1)`.
3. `session.Present(older', newer, p.Alpha, p.MotionAlpha)`; `HasPresented = true`.
4. Events with tick ≤ ⌊p.Tick⌋ (or held over 1 s) go into the replica world's own queues: `world.Sounds?.Enqueue`, `world.BloodMarks?.Enqueue`, `world.Floaters?.Enqueue` (null = consumer off, dropped); Game consumers need no change.
5. Clock: `sim.SetSpeed(LastSpeed ∈ {1,2,5} ? LastSpeed : 1)`, `if (Speed == 0) sim.SetSpeed(0)`, `sim.RequestedPause = HostBusy` (drives HUD, `PausedPanel`, title, `audio.SetPaused`, weather speed). On change, `jitter.SetRate(Speed == 0 || HostBusy ? 0 : 30 · AchievedSpeed, Rules.SendRate)`. `H = AnchorHours + (p.Tick − AnchorTick)/30 / GameClock.SecondsPerGameHour`, `d = (H − Clock.TotalHours) · SecondsPerGameHour`: `0 ≤ d < 3600` → `Clock.Advance(d)`; `−1 < d < 0` → hold; else `Clock.SetTime((long)Math.Floor(H/24), H % 24)`. Then `world.GameHours = Clock.TotalHours`.

**Uplink** (`EndFrame`): `world.Commands.TakeDue(long.MaxValue)` → when `Playing`, `Commands(epoch, nextSeq, …)` batches of ≤ 256 via `CommandCodec.Write` (else dropped); an `Ack` if none in 100 ms; `ViewUpdate(session.Camera.Current.Target, distance)` when moved > 50 units or every 1 s.

## 6. WP5: engine hooks

### `Meitou.Engine/Time/ClockRequest.cs` (new)

```csharp
public enum ClockRequestKind : byte { TogglePause, SetSpeed, Faster, Slower }
public readonly record struct ClockRequest(ClockRequestKind Kind, double Speed = 0)
{
    public static ClockRequest TogglePause => new(ClockRequestKind.TogglePause);
    public static ClockRequest Set(double speed) => new(ClockRequestKind.SetSpeed, speed);
    public static ClockRequest Faster => new(ClockRequestKind.Faster);
    public static ClockRequest Slower => new(ClockRequestKind.Slower);
    public void ApplyTo(SimulationClock clock);   // TogglePause()/SetSpeed(Speed)/Faster()/Slower(): today's behaviour
}
public interface IClockAuthority { void Request(ClockRequest request); }
```

### `WorldSession.cs`

```csharp
public enum SessionMode : byte { Local, Replica }
public SessionMode Mode { get; private set; }
public IClockAuthority? ClockAuthority { get; set; }
public bool ModalsPause => ClockAuthority is null;            // modal screens pause only single-player
public event Action<World>? SimulationTicked;                 // after each simulation tick (frame thread)
public event Action<World>? WorldReplaced;                    // after ReplaceWorld swapped (new world)
public float SimulationAlpha => Mode == SessionMode.Replica ? presentedAlpha : Simulation.Alpha;   // [0, 1]
public float MotionAlpha => Mode == SessionMode.Replica ? presentedMotion : Simulation.Alpha;      // positions and yaw; ≤ 2 on a replica
public void RequestClock(ClockRequest request) { if (ClockAuthority is { } a) a.Request(request); else request.ApplyTo(Simulation); }
public void Present(WorldSnapshot previous, WorldSnapshot current, float alpha, float motionAlpha);
   // Replica only: World.Present(previous, current); presentedAlpha = Clamp(alpha, 0, 1); presentedMotion = Clamp(motionAlpha, 0, 2)
public World ReplaceWorld(IEnumerable<ITickSystem> systems, Action<World>? setup = null, SessionMode mode = SessionMode.Local);
   // Replica: systems must be empty (ArgumentException); after setup next.MakeReplica(). Mode = mode; alphas reset to 0;
   // WorldReplaced?.Invoke(next) after the old world is discarded and the door gate reset.
```

Also: `Advance` runs the control ticks as today, then `if (Mode == SessionMode.Local) AdvanceSimulation(realSeconds);`; `AdvanceSimulation` throws on a replica; `SimulationTick` raises `SimulationTicked?.Invoke(World)` after `Clock.Advance`; the control tick's six clock lines become `RequestClock(ClockRequest.TogglePause | Set(1|2|5) | Faster | Slower)`.

### `Meitou.Simulation/World.cs` (not hashed, no golden effect)

```csharp
/// <summary>A net client's world (docs/net.md "Client"): it never ticks; <see cref="Present"/> shows the host's snapshots.</summary>
public bool IsReplica { get; private set; }
public void MakeReplica();    // systems.Count == 0 && Tick == 0, else InvalidOperationException
public void Present(WorldSnapshot previous, WorldSnapshot current);   // IsReplica, Phase Outside, current.Tick >= Tick: PreviousSnapshot, Snapshot, Tick = current.Tick
```

On a replica `RunTick` and `ApplyPausedInterfaceCommands` throw `InvalidOperationException("a replica world never ticks")`, and `PreparePopulation` returns `true`. `Tick` follows the host, so UI stamps (`Tick = session.World.Tick`) and `Steer`'s one-MoveOrder-per-tick keep working; the host restamps anyway.

**Game edits (single-player neutral)**:
- `PlayerInterface.Hud.cs` `PressButton` → `session.RequestClock(index == 0 ? ClockRequest.TogglePause : ClockRequest.Set(ButtonSpeeds[index]))`.
- `PlayerInterface.Faithful.cs:17` → `session.Simulation.RequestedPause = session.ModalsPause && (settingsOpen || faithfulScreens.HasModal);`.
- `PlayerInterface.Floaters.cs`: a field `readonly ConcurrentQueue<Floater> floaterQueue = new();`; the setter and `DrawFloaters` assign `world.Floaters = floaterQueue` (null when Off), never adopting the world's.
- `GameHost.Characters.cs`: `DrawnPosition` and `FillDrawListCore`'s position and yaw lerps use `session.MotionAlpha` (animation, weights and ragdoll keep `SimulationAlpha`). On a replica, a wrapped looping clip (`layer.Time < old.Time && layer.Length > 0`) unwraps: `time = old.Time + (layer.Time + layer.Length − old.Time)·alpha`, minus `Length` once reached (pairs can be several ticks apart).

## 7. WP6: game integration (`GameHost.Net.cs` plus edits)

```csharp
NetHost? netHost; NetClient? netClient;
NetIdentity NetIdentity();   // config.NetPlayerId ??= Guid.NewGuid() (saved); Name = SanitizeName(config.PlayerName ?? Environment.UserName);
                             // Build = $"{BuildVersion.Tag} ({BuildVersion.Commit})"; Data = cached DataSetIdentity.Compute(LoadOrder.FromInstall(install)), computed once on a worker
public void StartHosting(NetHostOptions options);  // refuses on the title, duel, sandbox or editor; new NetHost(session, new LiteNetTransport(), ...).Start(); events -> player.Notice / chat UI
public void BeginJoin(string host, int port, string? password);   // see below
void StopNet(string? reason);  // dispose; session.ClockAuthority = null; Simulation.RequestedPause = false; ShowTitle + Notice(reason) on a client
void NetBeginFrame(double dt) { netHost?.BeginFrame(); netClient?.BeginFrame(dt); }
void NetEndFrame() { netHost?.EndFrame(Covered); netClient?.EndFrame(); }
```

- **Loop** (`GameHost.Interactive.cs`): `NetBeginFrame(frameDt);` immediately before `if (!Covered) session.Advance(frameDt);` (`:422`); `NetEndFrame();` immediately after `StepSceneLoad();` (`:429`).
- **Join**: `StartSceneLoad` with stages "connect" (poll until `WorldReset` with IsJoin, or `Closed` → `SceneLoadFailed` notice, title stays), "world" (`BuildReplica`), "finishing" (no tick). `BuildReplica(reset)`, also the handler of every later `WorldReset`: `session.ReplaceWorld([], mode: SessionMode.Replica)`; `worldEpoch++`; `session.Clock.SetTime` from the reset's anchor hours; camera onto `reset.Focus`; `netClient.Attach(session)`; `netClient.Ready()` at once (snapshots flow during the warm-up); `BeginWarm()`. `CoveredFrame` readiness on a replica uses `netClient.HasPresented` in place of `PreparePopulation`.
- **Replica guards**: `SettleNewWorld` and `ResumeAfterCreation` call `RunTick` only when `Mode == Local`; `SendFocus` returns (the client sends `ViewUpdate`); `HandleSaves` consumes F5/F9 with `Notice("Only the host can save or load.")`; `UpdateNav` already returns without a population.
- **Host forwarding** (`WorldNews`): each message also `netHost?.PostMessage(-1, message)`; knockouts by owner `world.OwnerOf(k.Attacker)`: 0 keeps today's notice, > 0 → `netHost.PostMessage(owner, text, !k.Success)`.
- **Weather**: host `WeatherSource = () => gpu.Weather is { } w ? new(new WeatherTime((int)session.Clock.Day, session.Clock.HourOfDay), w.World.Snapshot()) : null` (sent on join and every 60 s); client `Weather += s => gpu.Weather?.Restore(s.Regions, s.Time.Day, s.Time.Hours)`.
- **Leaving**: a client's `Disconnected` → `mainMenuRequested` + `Notice(reason, true)`; `HandleMainMenu` and quitting → `StopNet("The host ended the game.")`.
- **Config/CLI**: `UserConfig` gets `Guid? NetPlayerId`, `string? PlayerName`, `int NetPort = 40120`, `string? LastJoinAddress` (no version bump). `GameOptions.Split` cases before `default` (`:109`) plus `Usage`: `--host [port]`, `--join <address[:port]>`, `--password <p>`, `--name <n>`, `--net-id new` (fresh GUID for a second instance on one machine). `--host` with `--new-game`/`--load` calls `StartHosting` on the first uncovered frame; `--join` boots to the title, then `BeginJoin`. `Meitou.Game.csproj` references `Meitou.Net`.

## 8. Cross-module interfaces

### Exposes

| To | Interface |
|---|---|
| D1, D2 | `Meitou.Net.Wire.NetWriter` and `NetReader` (§2); `NetProtocol.Version`, which they must bump on any codec change |
| D2 | `NetHost.ViewOf(int player) : Vector3?`; the host calls the encoder only on the frame thread between ticks |
| D2 | **Baselines:** any acked frame, not only the last sent, may be the baseline. Frames must be immutable after `Capture`, and `WriteDelta` must be a pure function of `(baseline, current)` |
| UI | `NetHost` and `NetClient` public APIs, events and stats (§4, §5); `NetPlayerInfo`, `RosterEntry`, `ChatLine`, `ClockChange`, `NetDisconnect`, `RejectCode` reasons |
| UI | `GameHost.StartHosting`, `BeginJoin` and `StopNet` (WP6) for the lobby screens |
| UI, Game | `WorldSession.Mode`, `SessionMode`, `ClockAuthority`, `ModalsPause`, `RequestClock(ClockRequest)`, `Present`, `SimulationAlpha`, `MotionAlpha`, `SimulationTicked`, `WorldReplaced`, `ReplaceWorld(..., SessionMode)` |
| UI, Game | `ClockRequest`, `ClockRequestKind`, `IClockAuthority`; `World.IsReplica`, `MakeReplica`, `Present` |

### Consumes

The signatures below are what this spec assumes. If an owner's spec differs, the owner wins and only `HostWorldLink` or `ClientWorldLink` changes.

D1, commands:

```csharp
public abstract record SimCommand { public int Player { get; init; } }   // 0 = host / single-player
public static class CommandCodec { public static void Write(NetWriter w, SimCommand c); public static SimCommand Read(NetReader r); }   // throws InvalidDataException
public static class CommandGate { public static SimCommand? Admit(World world, int player, SimCommand command, out string? reason); }
   // returns a copy with Player = player and client trust flags stripped, or null with a readable reason; frame thread, between ticks
```

D2, replication (namespace `Meitou.Net.Replication`):

```csharp
public abstract class ReplicationFrame { public abstract long Tick { get; } }
public readonly record struct BulkView(ushort Kind, long Revision, ReadOnlyMemory<byte> Payload);
public interface IViewerEncoder
{
    ReplicationFrame Capture(WorldSnapshot viewerSnapshot);
    void WriteDelta(NetWriter w, ReplicationFrame? baseline, ReplicationFrame current);   // ≤ 255 × ~1180 bytes after framing
    void CollectBulk(WorldSnapshot viewerSnapshot, List<BulkView> changed);               // all of them after Reset
    void Reset();
}
public interface IViewerDecoder
{
    ReplicationFrame ReadDelta(NetReader r, ReplicationFrame? baseline);
    void ApplyBulk(BulkView view);
    WorldSnapshot Restore(ReplicationFrame frame);   // Tick = frame.Tick; host CharacterIds; AnimationLayer.Length/Definition filled
    void Reset();
}
public static class ReplicationCodec { public static IViewerEncoder CreateEncoder(int player); public static IViewerDecoder CreateDecoder(ReplicaCatalog catalog); }
```

D2 builds the client's `ReplicaCatalog` (database, animation library, appearance builder) from the install; WP6 passes `ReplicationCodec.CreateDecoder(catalog)` to `new NetClient(...)`, and tests pass any decoder.

Simulation, per-player sessions:

```csharp
public WorldSnapshot World.PublishFor(int player);   // between ticks; PublishHeal and other publish side effects not repeated per viewer
public void World.EnsureSession(int player);         // idempotent, at a tick boundary; adds hashed terms only when > 1 session
public int World.OwnerOf(CharacterId id);            // -1 none
public Vector3? World.FocusOf(int player);           // the player's first living owned character (Characters.Previous position)
```

Save:

```csharp
public sealed record KnownPlayer(Guid Id, int Number, string Name);
public List<KnownPlayer> World.KnownPlayers { get; }   // loaded from and written to the save
```

## 9. Tests and docs

### Approved (in-process host and client over `LoopbackNetwork` with a `ManualNetClock`)

`tests/Meitou.Tests/Net/SessionTests.cs`: host `WorldSession` built with `SyntheticTown`'s systems and database (1 thread, synchronous paths, `PublishSnapshots = true`, `FocusCommand` at tick 0); client a replica `WorldSession` without systems; each frame `host.BeginFrame` → `hostSession.Advance` → `host.EndFrame` → `client.BeginFrame` → `client.EndFrame`.
1. Ownership: a `MoveOrder` for the client's own character moves it on the host; one for the host's character is rejected and the client gets a `Message` with the gate's reason.
2. Replication: at zero latency the client's restored newest frame equals the host's `PublishFor(player)` at that tick (ids, positions, yaw, animation layers; D2's comparer for views).
3. Clock: a client `TogglePause` pauses the host and raises `ClockChanged` with the client's name on both sides; `HostOnly` refuses it with a message.
4. Reconnect: sever, reconnect with the same GUID → same number, squads still owned, full snapshot (`baseSeq == 0`).

`GoldenHashTests` and the full suite must pass unchanged.

### Proposed (need the user's approval before writing)

- `LoopbackTransportTests`: reliable order under jitter, seeded loss; every session test relies on that transport.
- `HandshakeTests`: each `RejectCode` reason, number stability across a world swap; branches the session tests never reach.
- `JitterBufferTests`: pair and alpha under jitter, starvation cap, pause hold, teleport snap; timing logic otherwise seen only in the game.
- `EventTapTests`: the host's own consumer still gets every event while the tap forwards it (the no-stealing invariant).

### Docs

New `docs/net.md` (linked under Implementation in `docs/README.md`): roles and modes, channel/message table, handshake, reasons and player numbers, host frame and the event-tap invariant, client presentation algorithm and constants, clock policy and anchors, limits; all engine choices. `docs/engine.md` "Loop": the two pump points, clock input via `RequestClock`. `docs/simulation.md` "Time model": replica worlds never tick (`MakeReplica`, `Present`); `SimulationTicked`.

## 10. Edge cases and what to leave alone

- Host covered (loading): `HostBusy` broadcast, clients show paused; commands wait in the queue and a world swap drops them, as in single-player.
- Speed 5 with dropped ticks: playback rate `30 · AchievedSpeed`; the live estimator absorbs drift. Resume after pause: `SetRate` clears samples, so playback waits about one delay.
- Out-of-order and old-epoch packets are dropped by epoch and sequence (`u32` per connection, no wrap in practice; `u16` epochs compared for equality only).
- Leave alone: simulation systems, hashing and save format; `Publish()` internals; D1/D2 codecs; lobby, chat and player-list screens (UI module, consuming §8). Out of scope: NAT punch-through, headless servers, PvP, persisted bans.
