# Multiplayer network layer

`src/Meitou.Net` is the wire format and session code of host-authoritative co-op: one listen-server host, up to
`NetProtocol.MaxPlayers` (8, host included) clients, UDP through LiteNetLib. Sessions, the command gate, per-viewer
publish and replica worlds are in [simulation](simulation.md) ("Multiplayer"); frame order and client boot in
[engine](engine.md); windows and keys in [meitou-ui](meitou-ui.md). `Meitou.Net` references Core, Data, Simulation and
Engine only, never Rendering, Audio, Ui or Game; only `Transport/LiteNetTransport.cs` touches LiteNetLib types.

This is the engine's own protocol, not a Kenshi format. **Verified** below means a test in `tests/Meitou.Tests/Net/`
pins it; everything else is the contract the codecs are written to. Traffic is unencrypted; a password is only an HMAC
proof at the handshake (co-op among friends).

## Channels

`ChannelsCount = 4`; reliable channels are ordered only within themselves, so each has its own string table.

| Channel | Delivery | Direction | Carries |
|---|---|---|---|
| 0 `Snapshots` | Sequenced | host to client | `SnapshotPart` |
| 0 `Snapshots` | Sequenced | client to host | `ClientFrame` |
| 1 `Control` | ReliableOrdered | both | handshake, clock, rules, weather, roster, commands, results, chat requests |
| 2 `Views` | ReliableOrdered | host to client | `ViewUpdate`, `LookDefinition` |
| 3 `Events` | ReliableOrdered | host to client | `EventBatch` (apart so a 40 KB research view cannot hold events back) |

Snapshot parts stand alone and are ordered by `Seq`; a late part dropped by sequencing costs only its entities. String
tables are kept for the whole connection, never reset at a world epoch.

## Messages

The first byte of every packet is a `MessageType`. The table is append-only within a protocol version; any layout change
in any codec bumps `NetProtocol.Version` (currently 1).

| Id | Message | Channel |
|---|---|---|
| 0x01-0x0B | Challenge, Hello, Welcome, Reject, Ready, WorldReset, Roster, ClockRequest, ClockState, Rules, Weather | Control |
| 0x10 / 0x11 | SnapshotPart / ClientFrame | Snapshots |
| 0x20 / 0x21 / 0x22 | CommandBatch / CommandResult / ChatRequest | Control |
| 0x30 / 0x31 | ViewUpdate / LookDefinition | Views |
| 0x40 | EventBatch | Events |

Epoch-scoped messages carry the world epoch (u16); a message of another epoch is dropped. `ViewState` (eye, focus,
character draw distance; `Replication/Views/ViewState.cs`) is what `ClientFrame` carries and what the event filter and
interest use.

## Primitives

`NetWriter` (array-pool buffer, reusable) and `NetReader` (`ref struct` over a span) are byte-aligned; there is no bit
stream. Flags are packed into varints.

- Fixed-width values are little-endian. Varints are LEB128 (at most 5 bytes for 32 bits, 10 for 64); signed varints are
  zigzag. Floats and doubles are raw bits, so NaN payloads and -0 survive; `Half` is 2 raw bytes. **Verified**
  (`PrimitivesTests`: varint and zigzag edge values, raw float bits).
- A string is a varint byte count then UTF-8. The writer replaces a lone surrogate with U+FFFD; the reader decodes
  strictly. A `Guid` is 16 bytes in the RFC (string-order) layout. `WriteBytes` writes no length.
- **Enums** travel by underlying type (`NetWriter.WriteEnum`, `NetReader.ReadEnum`): byte and sbyte raw, ushort, uint
  and ulong as a varint, short, int and long as a zigzag varint. The reader accepts a defined member or, for a `[Flags]`
  enum, bits some member uses. (`TaskType` is zigzag, `CharacterOrders` a varint, `SquadAi` a byte.)

**Reader rules.** Every failure throws `NetFormatException` and nothing else: an overrun, an overlong varint, a bool
byte other than 0 or 1, invalid UTF-8, a count over its limit, an undefined enum value, a non-finite float where
`ReadFinite` is used (every vector, quaternion and nullable float), trailing bytes at `ExpectEnd`. On channel 0 the part
is dropped; on channels 1-3 the stream and string-table state are lost, so the peer is disconnected ("protocol error").
Anything a client sends is read with these limits; the client reads host data the same way.

## Limits

`NetLimits` (bytes unless noted): `MaxIdList` 1024 ids, `MaxString` 4096, `MaxName` 256, `MaxMessageText` 1024,
`MaxChat` 1024 (the host clamps chat to 200 characters), `MaxListItems` 4096, `MaxViewItems` 16384 per keyed view,
`MaxPlans` 256, `MaxFcsMapEntries` 4096, `MaxCommandBatch` 64 commands, `MaxReliableMessage` 65536, `MaxViewBody` 16384
(soft), `MaxParts` 64, `MaxAnimLayers` 8, `MaxBones` 512, `MaxStringTable` 16384 entries. Codecs also check list and map
counts when writing (`ArgumentException`); a string over its byte limit is refused by the reader only.

## Quantization

`Quant` holds pure functions; the host compares quantized values, so a change below one step is never sent and the
client draws exactly what it decodes. NaN encodes as the zero value and out-of-range values clamp.

| Value | Wire | Notes |
|---|---|---|
| World position | `Q64`: `round(v * 64)` as an int, clamped to +-2^30 | 1/64 is a float32 step at +-`WorldLayout.HalfWorldSize` (147 456) |
| Yaw | `Yaw16`: i16 `round(yaw / pi * 32768)`, wrapped | decodes into [-pi, pi) |
| Unit range | `U16(v, min, max)` | clamped |
| Signed fixed | `S12`: i16 `round(v * 4096)` | about [-8, 8) |
| Rotation | `SmallestThree`: u32 | largest component dropped and made positive (2-bit index), others as signed 10-bit steps of 1/(511 * sqrt 2); angle error below 0.005 rad (**Observed**: 0.0045 worst over 3 million random rotations, 0.0021 for a single-axis turn) |
| Unit direction | `Dir16`: 3 x i16 (c * 32767) | renormalised on decode |

## String tables

One `StringTableWriter`/`StringTableReader` pair per channel and direction. The wire value is a varint `h`: 0 null; 1 a
literal (not stored); even `h` a reference to index `h/2 - 1`; odd `h >= 3` a definition of index `(h - 3)/2` followed by
the text. A full table sends literals; `intern: false` (messages, chat, dialogue, long text) also writes a literal.

- Reliable channels (`confirmOnSend`): an index is known to the reader once written, so later messages reference it.
- Snapshot channel: an index counts as known only after `Confirm` (the part that defined it was acked); until then every
  part using it defines it again, once per part (`BeginPart`). A redefinition must carry the same text.
- `Mark`/`Rollback` withdraw what was written since the mark and free indices first allocated since then, because the
  reader never saw them. `PartDefinitions` lists the indices the current part defined.
- Readers accept indices in any order and any number of times; an unknown index, an index beyond the table, or a
  redefinition with other text is a format error. A referenced string must still fit the field's byte limit.

**Verified** (`PrimitivesTests`): define, reference, literal, null, rollback and confirm round trips on both kinds.

## Common encodings

`CommonCodec`: `CharacterId` is a zigzag slot then a varint generation (`None` = (-1, 0), two bytes); id lists are a count
(at most `MaxIdList`) then ids; `Vector2/3/4` and `Quaternion` (x, y, z, w) are raw floats read with `ReadFinite`; a
nullable is a presence byte then the value, except `NullableInt`: varint 0 for null, else `zigzag(v) + 1`.

## FCS records

`FcsRecordCodec` writes an `FcsRecord` (an `EditCharacter` appearance, a look's loadout) as: `Type` varint, `Id` zigzag,
`Name` (literal), `StringId`, `Flags` and `ByteSize` varints; `Bools` (count, keys, values packed 8 to a byte, low bit
first); `Floats`, `Ints`, `Vector3s`, `Vector4s`, `Strings`, `Filenames` (count, then key and value each); `References`
(category count; per category its key, a count, then target and three zigzag values); `Instances` (count; id, target,
position, rotation x/y/z/w, states). Keys and ids are interned, text values over 128 chars are literals. Map order is kept;
a reader refuses a duplicate key and a map over `MaxFcsMapEntries`. Floats are read finite. The writer throws `ArgumentException` for what the reader
would refuse: a name, key or id over `MaxName`, a string value over `MaxString`, a null string, a non-finite float, vector or quaternion. **Verified**
(`FcsRecordCodecTests`: a record with every section filled round-trips through reliable-table codecs, twice, field by field).

## Keyed views

`KeyedViewSync<TKey, TItem>` (host) and `KeyedViewDecoder<TItem>` (client) replicate a list view (buildings, ground
items, doors, ...) as `ViewUpdate` bodies, with `ViewKind` naming the view (1-25, append-only). Keys never go on the wire:
each key gets a slot number (a varint, never reused until `Reset`) and the body speaks in slots.

Body: a flags byte (bit 0: the order changed; other bits must be 0); if set, the new order as a count (at most
`MaxViewItems`) and slots, where a slot missing from it is removed; then the upserts as a count and (slot, item) pairs.
The decoder validates the whole body before changing anything and keeps its `Items` reference while nothing changed.

Change detection: a source reference equal to the last one fully sent is skipped; otherwise each item is encoded with a
literal-only string table (so table state cannot make equal items differ and nothing is defined for unsent items) and
compared with the bytes last sent. Membership or order changes and `structural` calls go at once; item-only changes wait
for `minInterval` since the last send. A body over `maxBody` (default `MaxViewBody`) holds the remaining items back for the
next call, always sending at least one; a source longer than `MaxViewItems` is cut to it. After `Reset` the next call
sends even an empty list.

## Command codec

`CommandTags` (`Commands/`) is the explicit, never reordered table of ushort tags, one per concrete `SimCommand` (81), each
with a `CommandOrigin`: `Client`, `Debug` (tags 200-206, accepted only when the host allows cheats) or `HostOnly`
(`SandboxMovement` 2, `RepathCommand` 9, `StartContract` 240, `JoinPlayer` 241, `TransferSquad` 242). Tags: 1-9 basics, 20-22
orders, 30-36 squads, 40-45 carry/combat, 50-53 first aid, 60-64 dialogue/editor, 70-74 trade, 80-94 inventory/locks,
100-116 production/construction. The abstract `SimCommand`, `DebugCommand` and `SquadCommand` have none.

**Layout** (`CommandCodec`): varint tag, the constructor parameters in declaration order, then the init-only properties in
declaration order (`MoveOrder.Queued`, `AttackOrder.Defensive`, `EditCharacter.Surgeon/Paid`, `BuyItem.Section/X/Y/Equip`,
`SellItem.X/Y`). `Tick` and `Player` are not sent: `Read(ref r, strings, tick, player, allowDebug)` sets them (the host
passes its own tick and the sender's number). Ints are zigzag varints, floats raw (read finite), enums by `NetWriter.WriteEnum`
(`CharacterOrders` varint, `SquadAi`/`ShackleWork`/`EditKind` byte, `TaskType` zigzag), `byte?` and `bool?` a presence
byte then the value, `float?` presence byte then raw, `SellItem.X` as `NullableInt`. Record, site, fixture, building and
instance keys are interned; names (squad, character) are literals. `InventoryItemToken` is index, record, section, x, y,
quantity, then the quality as **raw** float bits (tokens are compared by equality). `PlannedBuilding` is record, position,
rotation (x, y, z, w), floor, parent (nullable key); at most `MaxPlans` per `PlaceBuildings`. `EditCharacter.Appearance` goes
through `FcsRecordCodec`. The codec carries every field, trust fields included (`Defensive`, `Paid`); the gate strips them.

**Refusals.** `Read` throws `NetFormatException` for a `HostOnly` tag always, a `Debug` tag unless `allowDebug`, an unknown
tag, a non-finite float, an undefined enum value, a null where a string is required, or an over-limit count; on the Control
channel that drops the sender (`ProtocolError`). `Write` accepts every tagged type (host-only ones included, so the layouts
stay testable) and throws `ArgumentException` for an untagged type or an over-limit appearance, leaving the writer and string table as they were.

**Writer-side limits.** A reader refuses what a writer must therefore never produce, so the writer clamps: strings are cut
to `MaxName` bytes on a character boundary, id lists to `MaxIdList`, plans to `MaxPlans`, a non-finite float is sent as 0.

**Batches** (`CommandBatchCodec`, bodies of messages 0x20 and 0x21, without the `MessageType` byte the caller adds):
`CommandBatch` = epoch u16, first seq varint, count (at most `MaxCommandBatch`), the commands; command `i` is
`FirstSeq + i`. `Write` throws `ArgumentException` for more than 64 commands or a message (body plus the type byte) over `MaxReliableMessage` and leaves
the writer and string table as they were (`Truncate`, `Rollback`); the sender splits. `CommandResult` = seq varint, reason
(UTF-8, clamped to `MaxMessageText`); the host sends it only for refusals. Both batch ends use the Control channel's string
tables, one per direction.

**Verified** (`CommandCodecTests`): reflection finds a tag for every concrete command and none for the abstract ones; every
command, with every constructor parameter and init property set non-default, round-trips field by field with `Tick` and
`Player` set; host-only and debug tags are refused as above; a batch and a result round-trip through shared string tables.

## Transport

`Transport/` (`Meitou.Net.Transport`): `INetServerTransport` (`Listen(port)`, 0 = any free port, `Port`) and `INetClientTransport`
(`Connect(host, port, connectData)`) share `INetTransport`: `Poll(INetListener)`, `Send(peer, channel, data)`, `Drop(peer, payload)`,
`MaxUnreliablePayload(peer)`, `Stats(peer)` (`NetLinkStats`: round trip, loss, bytes), `Address(peer)`. `PeerSink(transport, peer)` adapts
one peer to `IPacketSink`. All calls are made from the frame thread.

- **Events** reach the `INetListener` only inside `Poll`, on the calling thread; nothing sent during a poll is delivered in it. Server:
  `Request(address, connectData)` returns null to accept (`Connected` follows) or a refusal payload. `Received` hands one whole packet.
  `Dropped(peer, reason, data)` comes exactly once per accepted peer and per `Connect`, also after a local `Drop` (reason `Closed`, empty
  data, at the next poll), never after `Dispose`. `NetDropReason`: `Closed` (the remote side gets the `Drop` payload), `Timeout` (silence
  for the disconnect timeout, an unanswered connect, a severed loopback link), `Rejected` (data = the refusal), `Error` (unknown host,
  socket error). `Connect` never throws for network failures; they end in `Dropped`.
- **Ids** (`NetConnectionId`) start at 1 and are never reused within a transport instance, so a late event cannot name a newer peer.
- **Send** copies the data and is ignored for a peer that is not connected (pending, dropped or unknown); a `Snapshots` payload over
  `MaxUnreliablePayload` or a reliable one over `MaxReliableMessage` throws `ArgumentException`. `MaxUnreliablePayload`, `Address` and
  `Stats` give 0, "" and default for an unknown peer. `Address` and `Request`'s address are the IP without the port (address bans).
- **Drop** payloads and connect refusals are at most `INetTransport.MaxDropPayload` (1000) bytes, else `ArgumentException`; writers clamp
  (a `Reject` reason included). Data sent before a `Drop` and not yet delivered may be lost, so what the remote must learn goes in the payload.

**`LiteNetTransport`** (one class for both sides; the only user of LiteNetLib 2.1.4): one `NetManager` with `ChannelsCount = 4`,
`UnsyncedEvents = false`, `AutoRecycle`, `EnableStatistics`, `DisconnectTimeout = 15000` ms, `IPv6Enabled`. `Snapshots` is
`DeliveryMethod.Sequenced` (may drop, never duplicates, a packet older than one delivered is dropped), the others `ReliableOrdered`
(fragmented by LiteNetLib). Connect data travels raw (`Connect(IPEndPoint, ReadOnlySpan<byte>)`, `ConnectionRequest.Data`; `Accept()` /
`Reject(bytes)`); a host name resolves through DNS (IPv4 first). `MaxUnreliablePayload` = `GetMaxSinglePacketSize(Sequenced)`;
`Drop` = `NetPeer.Disconnect(bytes)`, read back from `DisconnectInfo.AdditionalData`. `MaxFragmentsCount` = 65, enough for one
`MaxReliableMessage` at 1014 bytes per fragment, so a peer cannot make the receiver hold larger reassemblies (the default 65535 allocates
a 65535-slot table per hostile fragment). `Poll` runs `PollEvents` then `TriggerUpdate`, so what was queued since the last poll leaves
without waiting for the next 15 ms update. **Observed** (2.1.4 assembly): MTU discovery is off by default, so the MTU stays 1024 and
`MaxUnreliablePayload` is 1020 (4-byte channeled header); `Disconnect`/`Reject` data that does not fit one 1024-byte datagram with its
9-byte header is silently blanked; `Disconnect` discards queued, unsent reliable packets; a pending connect fails after 10 × 500 ms.
**Observed** (2.1.4 XML docs): `Start` runs LiteNetLib's own logic thread, which keeps links alive and resends while the frame loop is
blocked (a long scene-load stage); queued events wait for the next `Poll`. Every member used is checked by compiling against the 2.1.4 package. **Verified** (`LiteNetTransportTests`:
connect payload, Control, Snapshots and a 5000-byte fragmented Events packet, and a drop payload over UDP on 127.0.0.1, OS-assigned port).

**`LoopbackNetwork(LoopbackOptions, INetClock)`** (tests): one `Server()`, any number of `Client()`s, `Sever(clientSideId)` (both ends time
out at their next poll). Deterministic: one `Random(Seed)` drawn in send order, delivery by (due time, send order) from the clock (usually
`ManualNetClock`). Each packet is due at send + `LatencyMs` (data + uniform 0..`JitterMs`). Reliable channels never lose: each lost
transmission (`ReliableLoss`) delays the packet by one resend (2 × latency + jitter, at least 1 ms) and order per channel holds later ones
behind it. `Snapshots` loses (`UnreliableLoss`), reorders through jitter and duplicates (`Duplicate`); the receiver drops anything sent
before the newest packet it delivered. Data never overtakes the accept; a close is due one latency later and the closing end's data
still in flight is lost (also on `Dispose` and `Sever`). A payload over `Mtu` (default 1200) throws; a connect to a port nobody listens on
times out one round trip later, and requests in flight to a disposed server one latency later. **Verified**
(`LoopbackTransportTests`: accept and refusal with payloads, complete per-channel order under 30 % loss and jitter, sequencing never
delivers an older packet, drops reach both sides once).

**Clocks**: `INetClock.NowMicroseconds`; `StopwatchNetClock` (monotonic, 0 at construction), `ManualNetClock` (`NowMicroseconds` settable,
`Advance(TimeSpan)`).

## Snapshot core

`CharacterWire` (`Replication/Snapshot/`) holds the quantized fields of one `CharacterSnapshot` in 13 groups, each one bit
of the change-mask varint of a record, hottest first. The host quantizes each per-viewer snapshot (`Quantize`) and diffs
it against the last acked wire (`DiffMask`); the client keeps wires and rebuilds snapshots (`ToSnapshot`). Every stored
value is what the client decodes, so `Quantize(ToSnapshot(w))` equals `w` in every group (**Verified**,
`CharacterWireTests`). The record header (slot, length, baseline back-reference, mask) belongs to the delta layer; the
codec (`CharacterWireCodec.WriteGroups`/`ReadGroups`) writes the groups the mask names, in bit order, onto the baseline
(`CharacterWire.Default` without one).

| Bit | Group | Wire |
|---|---|---|
| 0 | Position | `Q64` x3 as zigzag differences from the baseline, absolute without one |
| 1 | Yaw | `Yaw16` |
| 2 | Animations | below |
| 3 | Flags | varint of 13 bools (WeaponDrawn, IsPlayer, IsTrader, Selected, Inspected, PickingLock, WorkingShackles, InBed, InCombat, Shackled, ShacklesLocked, CanBeShackled, CarriesShackles), Task, Orders, RunSpeed byte, PortraitSerial varint |
| 4 | Body | presence; Blood `U16[0,1]`; WorstPart `S12`; Hunger `U16[0,4]`; varint of Unconscious, Dead, NeedsAid, Care (2 bits), MedicJob, RescueJob, NeedsSplint |
| 5 | Carry | CarriedBy, Carrying, AttachBone |
| 6 | Stealth | byte 0 or 1 + level; KO chance; up to 32 watchers (`Q64` relative to the character, level) |
| 7 | Ragdoll | presence, then `RagdollWire` |
| 8 | Look | `LookId` varint (0 none; the client rebuilds `Appearance`, never sent), Scale f32 |
| 9 | Identity | Name, Faction, SquadId, Owner (zigzag), SurgeryCost (`NullableInt`), Activity |
| 10 | Prison | presence, Locked, Sentence f32, remaining hours as zigzag minutes, Bail |
| 11 | Turret | presence, Key, BuildingId, GunId, `Q64` position, `Yaw16` |
| 12 | Jobs | up to 64 `PermaJob`: Type (zigzag), Site, Saved (`Hand` presence + 5 zigzag; absent for `Hand.Null`) |

Together with the id in the record header these cover all 48 `CharacterSnapshot` members once the detail fields (Skills,
AllSkills, SkillXpMultipliers, Hud, Bounties, HealthParts, ItemGrid, Inventory, Path) go on the reliable views. Blood and
hunger are quantized so a slow drain is not resent every tick.

**Animations.** A count byte (at most `MaxAnimLayers`), then per layer a flags byte (b0 definition differs from the
baseline's layer at the same index, b1 weight differs, b2 length differs, b3 raw time, b4 raw weight) and the fields it
announces: `Definition + 1` varint (0: a name follows, the fallback layer or an index the library lacks), weight `U16[0,1]`
(raw f32 outside [0,1]), length f32, then the time as a u16 phase `round(Time / Length * 65535)` (raw f32 unless
`Length > 0` and `0 <= Time <= Length`). A steady layer costs 3 bytes. The client takes `Name` from its library, so both
ends must share `AnimationLibrary` (`AnimationHash`). Phase error is at most 1/65535 of the clip, which keeps footstep
fractions exact (audio reads `Time / Length`) and `CharacterSounds`' dominant layer.

**Ragdoll** (`RagdollWire`). Bone count (at most `MaxBones`), the `Set` bitset, weight `U16[0,1]`, a flags byte (b0 wide),
then for each set bone a smallest-three rotation (u32) and a position: three i16 at 1/256 unit, or raw floats for the
whole pose ("wide") when any position rounds to 127 units or more (large animals). Every set position is sent although only
root bones use theirs, because the codec cannot see the rig. The decoder keeps the dropped rotation component strictly the
largest, so packing a decoded rotation gives the same bits (near-equal components would otherwise flip the index).

**Projectiles** (`ProjectileWire`). Keyed by id with the same baselines as characters; the delta layer writes the id and
back-reference. Body: a flags byte (bits 0-3 which of position, direction, stretch, mesh follow; bit 4 the stuck value),
the `Q64` position as zigzag differences, the direction as three i16 (settled so decoding and re-quantizing keep them),
stretch f32, mesh (interned, sent when it differs from the baseline's, so for a new projectile).

**Writer-side limits.** Text is cut to `MaxName` bytes at a character boundary, non-finite floats become 0, and layers (8),
watchers (32), jobs (64) and ragdoll bones (a larger pose is dropped) are cut, so a reader never refuses what `Quantize`
produced. A job whose `TaskType` is undefined is dropped. A layer with an out-of-library definition is sent by name.
`Quantize` takes the look mapping as a delegate and the `AnimationLibrary`, so this layer does not depend on the look
registry. **Verified** (`CharacterWireTests`: full and delta round trips, hostile input cut to limits, idempotence over
random snapshots, smallest-three error at most 0.005 rad, ragdoll position error half a 1/256 step, phase and yaw steps).

## World and production views

`ViewCodecs` (partial static class, `Replication/Views/ViewCodecs.{World,Production}.cs`) holds the item codecs and the
host/client halves of views 5-14. Fields go in declaration order (positional, then init properties); bools are packed
into one flags byte (the reader refuses unknown bits); floats are raw and a non-finite one is written as 0 so
`ReadFinite` never refuses a writer's output; ids and names are interned, `ResearchOption.Description` is a literal;
lists are a count then items. Writers clamp strings to their byte limit (`MaxName`, `MaxString` for descriptions)
without splitting a surrogate pair, and lists to `MaxListItems`.

| Kind | View | Key | Min interval | Host / client half |
|---|---|---|---|---|
| 5 | Buildings (`BuildingSnapshot`, `MaterialView`s, Dismantling, WallParent) | `Id` | 200 ms | `BuildingsSync` / `BuildingsDecoder` |
| 6 | GroundItems | `Id` (int) | 0 | `GroundItemsSync` / `GroundItemsDecoder` |
| 7 | Containers (`ContainerSpot`) | `Id` | 1 s | `ContainersSync` / `ContainersDecoder` |
| 8 | DoorPicks | `Id` | 1 s | `DoorPicksSync` / `DoorPicksDecoder` |
| 9 | Doors (`DoorEntry`: Building, Door, `DoorView`) | (Building, Door) | 0 | `DoorsSync` / `DoorsDecoder` (dictionary) |
| 10 | Unlocked (record ids, sent in ordinal order) | the string | 0 | `UnlockedSync` / `UnlockedDecoder` (set) |
| 11 | Inspected (`BuildingDetail?`: presence byte, instance, per door flags and level) | single | 0 | `InspectedSync` / `InspectedDecoder` |
| 12 | Machines (`MachineSnapshot`: slots, storage grid, farm with `PlantGrowth`, craft, power, sound) | `Site` | 200 ms | `MachinesSync` / `MachinesDecoder` |
| 13 | Research: header (levels, queue, rate, finished) then keyed `Available` | option `Id` | 200 ms | `ResearchSync` / `ResearchDecoder` |
| 14 | Crafts (`CraftEntry`: site and its `CraftOptionView`s) | site | 500 ms | `CraftsSync` / `CraftsDecoder` (dictionary) |

Sources that are not lists (doors, unlocked set, crafts) go through `WorldMappedSync`, which flattens them once per source
reference so the keyed sync keeps skipping an unchanged reference; the client's `WorldMappedDecoder.Value` keeps its
reference until a body changed something. Kind 11 uses `WorldSingleSync` (sent when its canonical bytes differ).
`ResearchViewSync` writes the header with every body and sends a header-only body (empty keyed part) when only the header
changed and the interval passed; a decode equal to `ResearchSnapshot.Empty` returns that instance. The callers pass
`BuildingsRevision` and `GroundItemsRevision` changes as `structural`. A machine's storage grid and craft output use
private inventory item codecs; kind 21 (N6) has its own.

**Verified** (`WorldViewCodecTests`): every kind round-trips with populated, non-default data; the keyed delta covers
insert, update (after the interval), remove and reorder; an equal list sends nothing and keeps the client's list reference.

## Player and detail views

`ViewCodecs.Player.cs` / `ViewCodecs.Detail.cs` (`partial class ViewCodecs`) hold the `ItemCodec<T>` of each player view;
the keyed kinds have factories (`CampSync`, `SquadSync`, `SpeechSync`, `StatsSync`, `HealthSync`, `InventorySync`,
`PathSync`) and are decoded with `KeyedViewDecoder`. The single kinds are plain bodies: N10 sends one when its bytes or
source reference changed. `PlayerCovered`/`DetailCovered` list the `WorldSnapshot`/`CharacterSnapshot` members these
views carry (for the coverage test).

| Kind | Body | Key, min interval |
|---|---|---|
| 1 Header | `HeaderView`: `SquadAi` byte, ActiveSquad, Viewer, PlayerFaction, Money (zigzag), InCreation, CreationRaces | single |
| 2 Towns | count (at most `MaxViewItems`), one `TownKnowledge` byte per town | single, 1 s |
| 3 Camps | Site, TownId, Name, FactionId, flags (Wildlife, has position), Known, Position? | Site, 1 s |
| 4 Squads | Id, Name, member ids (cut to `MaxIdList`) | Id, 0 |
| 15 Trade | presence, ids, TraderName, money, stock size, BuysStolen, Stock and Goods (13 positional fields, Material, Value, SellValue, Weight, flags for Stolen/Charge/FenceChance/FenceOdds) | single, nullable |
| 16 Conversation | presence, ids, names, Text and Choices (literals), PlayerMoney, Log as `kept` + new lines | single, nullable |
| 17 DialogueRequest | presence, Id, Kind, Player, Npc | single, nullable |
| 18 Speech | Speaker, Text (interned), Large | Speaker, 0 |
| 23 Relations | Revision, floats | single |
| 24 Talkable | id set, written in slot order so equal sets are equal bytes | single |
| 25 Prospected | presence, `ProspectReport` (Environment and Resources as name + float) | single, nullable |
| 19 Stats | per character: Skills?, AllSkills, SkillXpMultipliers, Hud? (+ Extended?), Bounties (`HoursLeft` raw double, may be infinity) | CharacterId, 100 ms |
| 20 Health | per character: `HealthPartView` list | CharacterId, 100 ms |
| 21 Inventory | per character: one-line `Inventory`, `ItemGrid?` (sizes, TotalWeight, CarryCapacity?, items with Material and Charge?) | CharacterId, 0 |
| 22 Path | per character: waypoints as `Q64` ints | CharacterId, 250 ms |

- **Conversation log.** The log only grows during a conversation, so `ConversationViewWriter` (one per client) sends how
  many leading lines equal the log last sent (`kept`) and then only the new ones; `Encode` returns false for an
  unchanged conversation. `ConversationViewReader` keeps the log between bodies and refuses `kept` beyond it. Both
  `Reset` at a new epoch or connection. A closed conversation (null) forgets the log.
- **Detail views.** `Skills`, `Hud`, `Bounties`, `HealthParts`, `ItemGrid`, `Inventory` and `Path` are empty on every
  character but the viewer's selection and the inspected one, so they leave the snapshot parts. `StatsOf`, `HealthOf`,
  `InventoryOf` and `PathOf` list the characters that have something; each detail record has `ApplyTo(CharacterSnapshot)`
  for the client; `HeaderView` and `RelationsView` have the same for `WorldSnapshot`.
- **Writer limits.** Interned strings are cut to `MaxName` (a reference must fit every field that can name the string),
  free text to `MaxString`; every list is cut to its limit; non-finite floats are written as 0. Single views are not split:
  a trade is cut only at `MaxListItems` items per list (**Unknown**: whether a real shop's trade nears `MaxReliableMessage`).
- **Verified** (`PlayerViewCodecTests`): every kind round-trips populated data, a second write of the decoded value gives
  identical bytes, the conversation log is sent incrementally, keyed kinds go through `KeyedViewSync`/`KeyedViewDecoder`.

## Looks

`CharacterSnapshot.Appearance` never crosses the wire: a part carries a look id (0 = none) and the client rebuilds the
look from the recipe of the `IAppearanceSource` call that made it (`Replication/Looks/`).

- **Recording.** `RecordingAppearances` wraps the host's source (every interactive run, so looks made before hosting
  have recipes). It forwards every member unchanged and keeps, weakly per returned look, `Rolled(recordId, factionId,
  seed)` or `Kit(recordId, loadout)`. An animal's look is its species' shared instance, so it is always
  `Rolled(animalId, null, 0)`. Every look in play is made through the source (population, `RefreshEquipment`, editing,
  save loading): **Observed** by reading the callers. `AppearanceRecipe.FromLook` covers a look the recorder never saw:
  the species for an animal, else a Kit from `look.Loadout`, else none (the client shows a marker).
- **Ids.** `LookRegistry.IdOf` gives each look object one id on first use, never reused within the registry (one per
  `NetHost`), and takes the recipe then (with `look.Shape` when set). Entries of collected looks are swept. Frame thread.
- **`LookDefinition`** (`0x31`, Views): id varint (never 0), recipe kind byte (1 Rolled, 2 Kit), body, then a shape
  presence bool. Ids go through the channel's string table, ints are zigzag. Rolled: record id, faction id (nullable),
  seed. Kit: record id; seed; character id (nullable); race id; flags (b0 female, b1 generated); morph index; the
  CHARACTER_APPEARANCE record (`FcsRecordCodec`); clothing (count, items); backpack (presence, item); carried (count,
  items); crossbow (presence, weapon); weapons (count, weapons); four limbs (varint 0 natural, else state + 1, then the
  prosthetic id?). Item: record, quality, material?. Weapon: weapon, manufacturer?, model?, level, section, row. Shape:
  flags (female, narrow left/right thigh), muscle, starvation, leg ratio (f32), hide stump (zigzag varint), missing
  limbs (bit mask byte), sliders (count, key and f32). `Loadout.Notes` are dropped. The writer refuses ids over `MaxName`, null required
  ids and over-long lists, masks hide stump to its defined bits and writes non-finite shape floats as 0. **Verified** (`LookTests`: round trip of every field, twice through a reliable table).
- **`LookCache`** (client, `ILookResolver`): kept for the connection; `Define` builds on the thread pool (synchronously
  with `background: false`), a repeated id keeps its first recipe, `Resolve` is null until built. A recipe naming a
  record the client lacks, or a build that throws, gives a null look (marker).
- Rolled and Kit recipes (with stumps, a crushed limb and a prosthetic leg), the `FromLook` fallback and animals rebuild
  the host's parts, meshes, head, hair, colours, poses, body layers and lights from a fresh `GeneratedAppearances`.
  **Verified** (`LookTests`, [Slow], base game data).

## Events

`EventBatch` (message `0x40`, Events channel) is `0x40 | Epoch u16 | Tick zigzag varint | count (at most 1024) | events`;
`EventCodec.Write` includes the type byte and `Read` checks it. One batch per peer per frame, only when non-empty. Each event is a
kind byte and its fields (**Verified**: `EventCodecTests`):

| Kind | Type | Fields |
|---|---|---|
| 1 | `SoundNet` (spatial) | SoundKind, Subject, Other, Position, flags (bit 0 Heavy, 1 AttackerUnarmed, 2 AttackerRobot, 3 Armoured, 4 Fatal, 5 has Item), Item? |
| 2 | `BloodNet` (spatial) | BloodMarkKind, Who, Position, Direction (2 halves), Amount (half), Colour (3 bytes, RGB), Race? (presence bool) |
| 3 | `FloaterNet` (spatial) | FloaterKind, Target, Position, flags (bit 0 TargetIsPlayer, 1 SourceIsPlayer), Part, `HitDamage` (5 floats), `ArmourAbsorbed` (3 floats), Healed |
| 4 | `MessageNet` | Text (at most 1024 B) |
| 5 | `KnockoutNet` | Tick (zigzag), Attacker, Target, Chance, Success |
| 6 | reserved | was the prospect report (now a snapshot field); the reader refuses it |
| 7 | `ChatNet` | From (zigzag), Name (at most 256 B), Text (at most 1024 B; the host clamps to 200 chars) |
| 8 | `NoticeNet` | NoticeKind (Joined, Left, TimedOut, Kicked, Banned), Player (zigzag), Name, Detail (at most 1024 B) |

Ids are `CommonCodec.WriteId`; Position is three zigzag varints of `Quant.Q64` (1/64 unit). Unknown kinds, unknown flag bits and
non-finite numbers are `NetFormatException`. The writer clamps instead: texts are cut to their byte limit on a character
boundary, NaN/infinite floats become 0, halves saturate, and a batch is cut to 1024 events. An enum value that is not a defined member
is an `ArgumentException` on write.

`EventFilter` (**Verified**): kinds 1-3 are spatial; a client wants one when its Position is within the radius of `ViewState.Eye`
(straight-line, `NetHostOptions.EventRadius`) or when its Subject, Other, Who or Target is in the client's interest set. Every other
kind is wanted by whoever the host module sent it to.

## Protocol and handshake

`Session/ProtocolCodec.cs` writes and reads the Control messages: `Write` puts the `MessageType` byte first, the caller
reads it (`ReadType`), and `ReadX` reads the rest and demands the end. `NetRules` and `SessionRules` overloads write
bodies only (flag bits packed in one byte). Writers clamp texts to the reader limit at a character boundary and throw
`ArgumentException` for a wrong-size fixed field (nonce 16 bytes, proof 0 or 32) or a non-finite clock or weather number; a Reject reason is cut to
`MaxRejectReason` (996 bytes) so an encoded Reject fits `MaxDropPayload`, so a reader never refuses what a writer
produced. Doubles are read with a finite check. Limits: build 64 bytes, data set 1024 files, roster 256, weather
regions 256. **Verified** (`ProtocolCodecTests`: every message round-trips, including `WorldReset`, `SessionRules` and
`WeatherSync`; clamping, trailing bytes, NaN, bad type bytes).

Sequence: connect payload `Magic + u16 Version` (`CheckRequest`) -> `Challenge(Nonce, HostName, NeedsPassword)` ->
`Hello` -> `Welcome` (number, `NetRules`, roster) -> the host's `WorldReset` -> client `Ready(epoch)`. A refusal is
`Reject(Code, Reason)`, also the payload of a drop.

`Handshake` (pure, shared by host and client). Check order, with the reason texts (**Verified**, `HandshakeTests`):

| Step | Code | Reason |
|---|---|---|
| connect magic or version | Protocol | `Incompatible network protocol (yours {c}, host's {h}). Update Meitou.` (bad magic: `Not a Meitou connection. Update Meitou.`) |
| `connected + 1 >= maxPlayers` | Full | `The game is full ({n} players).` (`connected` = remote players in the game) |
| address banned | Banned | `You are banned from this game.` |
| Hello protocol, player id banned | Protocol, Banned | as above |
| id equals the host's | Duplicate | `Your player id is already in this game.` |
| build differs (unless allowed) | Build | `Version mismatch: you run {c}, the host runs {h}.` |
| data set differs | Data | `Your game data differs from the host's. {Difference}` |
| `AnimationHash` differs | Data | `... The animation library is different.` |
| password set, proof empty / wrong | Password | `This game needs a password.` / `Wrong password.` |

The host's own checks come after: no free number (`NoSlot`: `No free player slot.`), a live peer with the same id
(`Duplicate` to the older: `Signed in from another place.`). Other drop texts: `Kicked by the host: {reason}`, `The host
ended the game.`, `Lost connection to the host.`, `Took too long to load.`.

- **Proof** = HMAC-SHA256, key SHA-256(UTF-8 password), data nonce + `Guid.ToByteArray()`; empty without a password;
  compared with `FixedTimeEquals`. **Verified**: differs per password, nonce and player.
- **`DataSetIdentity.Compute(LoadOrder)`**: per file the first 8 bytes of SHA-256 of the content (LE) and the length,
  in load order (`LoadOrder.FromInstall`: core files, then `mods.cfg`); an unreadable file is (0, -1). `Combined` = first
  8 bytes of SHA-256 over (name, hash, length). `Difference` is null when equal, else
  `Missing: ..; extra: ..; different version: ..` (5 names per group, then `(and n more)`; names compared ignoring
  case) or `Same mods in a different load order.` **Verified** with temp files.
- **`AnimationHash`** = FNV-style fold of `Rng.StableHash` of every `AnimationLibrary.Definitions` id in order, then the
  count (the wire carries library indices). Equal for two builds of the same data is **Unknown** until a game-install
  test checks it.
- **`SanitizeName`**: control characters dropped, trimmed, 24 characters (no split surrogate), empty -> `Player {n}`.
- **`PlayerColourRule.Assign(taken, preferred)`**: `preferred % 8` when free, else the lowest free, -1 when all 8 are
  taken; callers pass the player number (the host, 0, takes colour 0 first). **Verified**.

## Interest, delta, replicator and mirror

`Replication/Delta/`: `ClientReplicator` (host, one per client), `ClientMirror` (client), `ClientFrame`/`ClientFrameCodec`,
`SnapshotEncoderOptions` (SendHz 20, BytesPerSecond 262 144, BaselineWindow 60, History 128, KeepAliveHz 5) and
`ReplicaCatalog`. `ClientReplicator.Send(view, now, sink)` sends the interest set's look definitions, then changed views
(Views), then snapshot parts (Snapshots). Codec `ArgumentException`s (unencodable host data) and messages over
`MaxReliableMessage` are withdrawn (writer truncated, string table rolled back) and reported once through `Log`.

**Interest** (`InterestSet`, every send): every `IsPlayer` character; what the viewer's UI names (Selected, Inspected,
trade customer and trader, conversation and dialogue-request ids, `PlayerSquads` members); characters within
`R = min(DrawDistance, 8000) + 300` of the eye on X/Z, kept until beyond `R + 300`; one step of carrier/carried closure.
Weights: 1 for UI references, the viewer's own characters (`Owner`) and anything under 1500; 0.5 under 3500; else 0.25.
Projectiles use the same radius and hysteresis. Before the first `ClientFrame` the view is the origin with distance 0.

**SnapshotPart**: `0x10 | Epoch u16 | Seq | Tick (zigzag) | ViewSeq (last ViewUpdate sent) | PartIndex byte |
PartCount byte | removals: count, CharacterIds | projectiles: removal count, ids (zigzag), record count, records |
characters: count, records`. Character record: `slot (zigzag gap from the previous record) | length | back (0: no
baseline, the generation follows; else the baseline is Seq - back) | mask | groups` (`CharacterWireCodec`). Projectile
record: `id (zigzag gap) | length | back | ProjectileWire body`. Seq is per client, +1 per send that sends a part.

- **Baselines.** An entity deltas against the state the client acked for it when the ack is at most `BaselineWindow` Seqs
  old, or when nothing was sent for it since that ack (the mirror always keeps an entity's newest state). Otherwise, for a
  new entity, a resync request or a removal cancelled before its ack, the record is full (`back` 0, groups against
  `CharacterWire.Default`). An entity whose wire equals its baseline is not sent and its priority resets.
- **Order and budget.** Projectiles, then characters: full records first, then by accumulated weight (each send adds the
  weight to a changed entity, sending resets it). A send stops at `BytesPerSecond / SendHz` bytes (doubled for 2 s after
  a reset); what is left keeps its weight for the next send.
- **Packetizing.** Each record is encoded under a string-table mark; one that does not fit `MaxUnreliablePayload` rolls
  back and starts a new part (at most `MaxParts`). A record bigger than an empty part leaves its Ragdoll group out (the
  client keeps its pose); still too big, it is skipped. Pending removals ride in part 0 until a part carrying them is
  acked. With nothing to send, a header-only part goes out when views were sent or at `KeepAliveHz`, so the newest Tick
  can lag the host's by up to `SendHz / KeepAliveHz - 1` ticks while nothing changes.
- **Acks** (`OnClientFrame`, frames of another epoch ignored): each acked (Seq, part) still in the ring of the last
  `History` parts moves its entities' baselines forward (never back), finishes its removals (only those sent since the
  entity last left, so a late ack from before a return cannot end a newer removal) and `Confirm`s its string
  definitions. Resync slots drop the baseline and force a full record. Acks of parts sent before an entity (re)started
  (new, back from a pending removal, or resynced) set no baseline, so baselines only move forward.
- **Writer limits.** A part section never holds more than `MaxIdList` removals or records, and negative slots,
  generations or projectile ids are logged and skipped, so the mirror never refuses a part the host wrote.
- **Wire cache.** Quantized wires are cached per `CharacterSnapshot` object, shared by every client of one `LookRegistry`.

**ClientFrame**: `0x11 | Epoch u16 | FrameSeq | acks: count (<= 8), (Seq, 64-bit part mask) varints | Eye, Focus as Q64
zigzag | DrawDistance u16 | resync: count (<= 32), zigzag ints` (character slots; projectile id `n` as `-1 - n`). The
mirror acks its newest 8 Seqs every frame; `HasNewParts` tells the client a frame is due.

**Views** (`ViewSender`/`ViewReceiver`): `0x30 | Epoch u16 | kind | ViewSeq | Tick (zigzag) | body`, ViewSeq +1 per message
for the connection. Kinds 1, 2, 11, 15, 17, 23-25 use `WorldSingleSync`, 16 `ConversationViewWriter`, the rest the keyed
syncs; Speech lists only speakers in interest, detail views (19-22) only characters in interest. Buildings, GroundItems
and Machines append their revision (zigzag) after the body; a Buildings or GroundItems revision bump with no list change
sends an empty keyed body. After `Reset(epoch)` every view is sent again. `LookDefinition`s go before the first part that
names the look; the sent-look set is kept for the connection.

**Mirror** (`ClientMirror`):
- **Storage.** Per entity a Seq-ordered history of (Seq, Tick, id, wire or removal): the newest state is always kept,
  older ones while within 64 Seqs of the newest, and states older than a baseline a record names are dropped.
- **Decoding.** A part of another epoch is ignored, one that does not parse is dropped whole (`DroppedParts`). A record
  whose baseline is not held, or that does not decode, is skipped by its length and its slot asked for in full
  (`SkippedRecords`); its part is applied but not acked, so the host keeps its baselines and redefines the part's
  strings. Duplicates are ignored and parts may arrive in any order (plain unreliable delivery works too); a part 64 or
  more Seqs older than the newest is ignored.
- **Removals** apply unless a state at their Seq or newer, or a newer generation, is held for the slot. They are kept
  even for a character not held yet, so a late older part cannot bring back one the host has forgotten. A state at the
  same Seq replaces a removal, so a slot's new generation replaces the old character.
- **Assembly** (`TryAssemble`). A Seq is ready when all its parts arrived or a newer Seq has a part; the newest ready Seq
  is assembled: each entity's newest state at or before it, characters by slot, projectiles by id, detail views merged by
  `CharacterId`, the views whose Tick it reached committed first (in arrival order). Looks resolve through
  `ILookResolver`; until a look is built the character keeps its last one. An unchanged slot keeps its
  `CharacterSnapshot` reference. `TickOf(id)` is the Tick of the newest state held (an unchanged entity keeps its older
  Tick).
- **Views** are decoded on arrival and queued; a view of another epoch is decoded into throwaway decoders (so its string
  definitions are kept) and dropped. A malformed view throws (the client disconnects).
- **Reset(epoch)** clears entities, Seqs, views and resync requests on both ends; string tables and looks are kept.

**Verified** (`ReplicationTests`): over a seeded link losing 20 % of parts and frames, delaying 15 % of parts a round
(older Seqs after newer), duplicating 10 % and holding views back, 300 sends of a churning world (moves, renames, spawns,
despawns, slot reuse, bolts, view changes) then 40 quiet sends leave the mirror equal to the host's quantized interest set,
projectiles and views (header, towns, squads, buildings and revision, ground items and revision, speech, doors,
relations, talkable, stats and path details); a lost removal is resent until acked, a reused slot replaces the old
character, a cancelled removal sends a full record; a lost string definition is redefined until acked and then only
referenced; under a 700 B/send budget every character is updated within 20 ticks and the mirror catches up.
`WireCoverageTests`: every public `WorldSnapshot` and `CharacterSnapshot` property is claimed by a part, a character group
or a view kind, every group and kind carries something, and N6's covered lists agree. **Observed** (synthetic, 83 of 125
characters in interest, half moving each tick): about 1.2 KB of parts and 40 B of views per send on the lossy link,
about 26 B per send once nothing changes. Real scenes are **Unknown** (wave 7 logs them).

## Host

Pending (package N11).

## Client and jitter buffer

Pending (package N12).
