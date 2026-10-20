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
a reader refuses a duplicate key and a map over `MaxFcsMapEntries`. Floats are read finite. **Verified**
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

Pending (package N3).

## Snapshot core

Pending (package N4).

## World and production views

Pending (package N5).

## Player and detail views

Pending (package N6).

## Looks

Pending (package N7).

## Events

Pending (package N8).

## Protocol and handshake

Pending (package N9).

## Interest, delta, replicator and mirror

Pending (package N10).

## Host

Pending (package N11).

## Client and jitter buffer

Pending (package N12).
