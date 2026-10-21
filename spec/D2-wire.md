# D2 — Wire format (`src/Meitou.Net` codecs): implementation spec

Scope: every byte layout between host and clients, plus the packetizer and client mirror that use those layouts. Semantics belong to other modules: ownership and validation, per-viewer publish, session, transport. This module never touches `Meitou.Simulation` state, so the golden `StateHash` pins cannot move.

## 0. Decisions

- **Own primitives.** `NetWriter`/`NetReader` over spans, not LiteNetLib's `NetDataWriter`/`Reader`. Those have no zigzag varints and no hard limits, and they would tie the codecs to the transport. Tests need no sockets. LiteNetLib is used only by the transport code in the same project.
- **Byte-aligned.** Flags are packed into varints. No bit stream.
- **Channels** (`ChannelsCount = 4`):

  | Ch | Delivery | Direction | Carries |
  |---|---|---|---|
  | 0 | Sequenced | host → client | `SnapshotPart` |
  | 0 | Sequenced | client → host | `ClientFrame` |
  | 1 | ReliableOrdered | both | handshake, control, commands, chat requests |
  | 2 | ReliableOrdered | host → client | views and looks |
  | 3 | ReliableOrdered | host → client | events (kept apart so a 40 KB research view cannot hold them back) |
- **Parts stand alone.** Every unreliable part is self-contained and ordered by `Seq`, so it works over Sequenced or plain Unreliable delivery.
- **`Seq` vs `Tick`.** `Seq` (u32, per client, +1 per send) identifies a snapshot for acks and baselines. `Tick` only drives presentation: a paused game republishes the same tick with new content.
- **Versioning.** `ProtocolVersion` (`const ushort = 1`) is in Hello. Tag and kind tables are append-only within a version, and any layout change bumps it.

## 1. Project and files

`src/Meitou.Net/Meitou.Net.csproj`:
- references Core, Data, Simulation and Engine;
- adds `LiteNetLib` 1.3.1 (use the latest NuGet release that is at least two weeks old);
- sets `InternalsVisibleTo Meitou.Tests`.

Also:
- add the project to `MeitouClient.slnx` and to the test project's references;
- add LiteNetLib (MIT) to `THIRD_PARTY_NOTICES.md`;
- create `docs/net.md` (channels, message ids, quantization, limits, delta rules; facts only) and link it from `docs/README.md`.

```
Primitives/ NetWriter NetReader NetFormatException NetLimits Quant StringTable CommonCodec
Fcs/FcsRecordCodec   Messages/ MessageType Handshake ClientFrame IPacketSink
Commands/ CommandTags CommandCodec CommandCodec.Nested CommandBatch
Snapshot/ CharacterWire CharacterWireCodec AnimationWire RagdollWire ProjectileWire
Snapshot/Views/ ViewKind KeyedViewSync ViewCodecs.{World,Player,Production,Detail}
Snapshot/Looks/ AppearanceRecipe RecordingAppearances LookRegistry LookCache
Delta/ Interest SnapshotEncoder ViewEncoder ClientReplicator ClientMirror
Events/ NetEvent EventCodec EventFilter
```

## 2. Primitives (WP1)

```csharp
public sealed class NetFormatException(string message) : Exception(message);
public sealed class NetWriter   // ArrayPool-backed, reusable
{
    int Length; ReadOnlySpan<byte> Written; void Reset(); void Truncate(int length);
    void WriteByte(byte); void WriteBool(bool); void WriteVarUInt(ulong); void WriteVarInt(long /*zigzag*/);
    void WriteUInt16(ushort); void WriteUInt32(uint); void WriteSingle(float /*raw bits*/); void WriteDouble(double); void WriteHalf(Half);
    void WriteUtf8(string /*varint length + bytes*/); void WriteBytes(ReadOnlySpan<byte>);
}
public ref struct NetReader(ReadOnlySpan<byte> data)
{   // mirror reads, plus:
    float ReadFinite();  int ReadCount(int max);  string ReadUtf8(int maxBytes);  T ReadEnum<T>() where T : struct, Enum;  void ExpectEnd();
}
```

**Reader rules:**
- Every failure throws `NetFormatException` and nothing else:
  - overrun;
  - overlong varint (more than 5 bytes for 32-bit, 10 for 64-bit);
  - a bool byte that is not 0 or 1;
  - invalid UTF-8 (`new UTF8Encoding(false, true)`);
  - a count over its limit;
  - an undefined enum value. `ReadEnum` checks against a cached `FrozenSet` of defined values, or the OR-mask for `[Flags]` enums. This covers `TaskType`.
- On a failure:
  - **channel 0:** drop the part;
  - **channels 1–3:** the stream and string-table state is lost, so the host disconnects that client and the client disconnects ("protocol error").

**`NetLimits`:** MaxIdList 1024, MaxString 4096 B, MaxName 256 B, MaxMessageText 1024 B, MaxChat 512 B, MaxListItems 4096, MaxPlans 256, MaxFcsMapEntries 4096, MaxCommandBatch 64, MaxCommandMessage 64 KB, MaxParts 64, MaxAnimLayers 8, MaxBones 512, MaxStringTable 16384.

**`Quant`** (pure functions; the host compares *quantized* values, so changes below one step are never sent):

| Value | Wire | Notes |
|---|---|---|
| World position | `Q64(v) = (int)MathF.Round(v*64)` (NaN → 0, clamp ±2^30); decode `q/64f` | 1/64 is float32's step at ±`WorldLayout.HalfWorldSize` (147 456) |
| Yaw | `Yaw16`: i16 `round(yaw/π·32768)`, wrapped | decodes into [-π, π) |
| Unit range | `U16(v,min,max)`, clamped | |
| Signed fixed | `S12(v)` = i16 of v·4096, clamp ±8 | |
| Rotation | `SmallestThree(Quaternion)` packed in a u32 | normalize; the largest component is made positive and its index takes 2 bits; the other three ×√2 take 10 bits each; error ≈ 0.16° |
| Unit direction | 3 × i16 (·32767) | renormalized on decode |

**`StringTable`** has one instance per channel and direction, because reliable channels are ordered only within themselves.
- The wire value is a varint `h`:
  - `0`: null;
  - `1`: a literal (`WriteUtf8`), not stored;
  - even `h`: reference to index `h/2-1`;
  - odd `h ≥ 3`: defines index `(h-3)/2`, followed by the UTF-8 text.
- Once the table is full, the writer sends literals. An unknown index throws.

```csharp
public sealed class StringTableWriter(bool confirmOnSend)   // true on reliable channels
{
    void Write(NetWriter w, string? s, bool intern = true);  // intern=false for messages, chat, dialogue text
    void BeginPart(); int Mark(); void Rollback(int mark); IReadOnlyList<int> PartDefinitions { get; }
    void Confirm(IEnumerable<int> indices);                  // channel 0: called after an ack
}
public sealed class StringTableReader { string? Read(ref NetReader r, int maxBytes); }
```

On channel 0, an index counts as known only after a part that defined it was acked. Until then, every part that uses it defines it again, and repeated definitions are idempotent.

**`CommonCodec`:**
- `CharacterId` = zigzag Slot, then varint Generation (`None` = (-1, 0)).
- id lists = count ≤ MaxIdList, then the ids.
- `Vector2`, `Vector3` and `Quaternion` = raw f32, read with `ReadFinite`.
- a nullable = presence byte, then the value. Exception: `NullableInt` is varint 0 for null, else `zigzag(v)+1`.

**`FcsRecordCodec`** (used by `EditCharacter` and by looks) writes, in this order:
1. `Type` varint, `Id` zigzag, `Name`, `StringId`, `Flags` varint, `ByteSize` varint.
2. `Bools`: count, the keys, then the values packed 8 per byte.
3. `Floats` (key, raw f32), `Ints` (key, zigzag), `Vector3s` (key, 3×f32), `Vector4s` (key, 4×f32).
4. `Strings` and `Filenames` (key, interned value).
5. `References`: for each category, the key, a count, then entries of (target, v0, v1, v2) as zigzag.
6. `Instances`: Id, Target, position as 3×f32, rotation as x,y,z,w f32, then `States`.

Map keys are interned. Insertion order is kept, as `OrderedDictionary` keeps it. A duplicate key (`TryAdd` returns false) throws. Each map holds at most MaxFcsMapEntries.

## 3. Messages and handshake

The first byte of every packet is its type:

| Code | Message | Ch | Direction | Body owner |
|---|---|---|---|---|
| `0x01` | Hello | 1 | c→h | |
| `0x02` | Welcome | 1 | h→c | |
| `0x03` | Refuse | 1 | h→c | |
| `0x08` | Control | 1 | both | session module |
| `0x10` | SnapshotPart | 0 | h→c | |
| `0x11` | ClientFrame | 0 | c→h | |
| `0x20` | CommandBatch | 1 | c→h | |
| `0x21` | CommandResult | 1 | h→c | |
| `0x22` | ChatRequest | 1 | c→h | |
| `0x30` | ViewUpdate | 2 | h→c | |
| `0x31` | LookDefinition | 2 | h→c | |
| `0x40` | EventBatch | 3 | h→c | |

```csharp
public sealed record Hello(ushort Protocol, Guid PlayerGuid, string Name, ulong DataHash, ulong AnimationHash);
public sealed record Welcome(ushort Protocol, int Player, ulong DataHash, ulong AnimationHash, int SendHz, int BytesPerSecond);
public readonly record struct ViewState(Vector3 Eye, Vector3 Focus, float DrawDistance);
public sealed record ClientFrame(uint FrameSeq, IReadOnlyList<(uint Seq, ulong Parts)> Acks, ViewState View, IReadOnlyList<int> Resync);
public interface IPacketSink { int MaxUnreliablePayload { get; } void Send(byte channel, ReadOnlySpan<byte> packet); }
```

- **`AnimationHash`** = `Rng.StableHash` over `AnimationLibrary.Definitions` ids in order, plus their count. A mismatch refuses the join.
  - The library indices go on the wire. They are identical for identical data: **Verified** by reading `AnimationLibrary.FromDatabase`, which sorts each group by `StringId` (Ordinal) and appends new groups last.
- **`ClientFrame`**:
  - the acks for the last ≤ 8 Seqs as (Seq varint, 64-bit part mask varint), so acks are redundant;
  - Eye and Focus as `Q64` zigzag;
  - DrawDistance as u16;
  - Resync slots (≤ 32);
  - about 40 B, sent at the send rate.
- **`CommandResult`** = (Seq, Accepted, literal Reason).

## 4. Command codec (WP2)

```csharp
public enum CommandOrigin : byte { Client, Debug, HostOnly }
public static class CommandTags { IReadOnlyList<(ushort Tag, Type Type, CommandOrigin Origin)> All; ushort TagOf(Type); (Type, CommandOrigin) Of(ushort); }
public static class CommandCodec
{
    void Write(NetWriter w, StringTableWriter s, SimCommand c);   // unknown type: ArgumentException
    SimCommand Read(ref NetReader r, StringTableReader s, long tick, int player, bool allowDebug);
}
```

**Tag table** (explicit, never reordered; (H) = HostOnly):

| Tags | Commands |
|---|---|
| 1–9 | MoveOrder, SandboxMovement (H), FocusCommand, SelectCommand, InspectCommand, InspectBuildingCommand, SeenDiscoveriesCommand, StopCommand, RepathCommand (H) |
| 20–22 | OrdersCommand, RunSpeedCommand, SquadAiCommand |
| 30–36 | NewSquad, RenameSquad, RemoveSquad, MoveToSquad, ReorderSquad, ActiveSquad, Dismiss (the `…Command` records) |
| 40–45 | AttackOrder, PickUpOrder, PutDownOrder, LootOrder, StealthKoOrder, RescueJobCommand |
| 50–53 | TreatOrder, SplintOrder, RestOrder, MedicJobCommand |
| 60–64 | TalkOrder, DialogueChoice, EndConversation, EditCharacter, FinishCreation |
| 70–74 | OpenTrade, CloseTrade, BuyItem, SellItem, ArrangeStock |
| 80–94 | MoveInventoryItem, ArrangeInventory, ReadMapItem, PickUpItemOrder, OpenContainerOrder, CloseContainer, PickLockOrder, GetOutOrder, ToggleDoorsOrder, ToggleDoorLocksOrder, DoorOrder, PutShacklesOrder, ShackleOrder, PayBailOrder, PutInCageOrder |
| 100–116 | OperateOrder, AddJob, RemoveJob, MoveJob, PutInMachine, TakeFromMachine, QueueResearch, CancelResearch, QueueCraft, CancelCraft, RepeatCrafts, ProspectOrder, PlaceBuildings, BuildOrder, DismantleOrder, CancelSite, PauseSite |
| 200–206 | DebugMoney, DebugTeleport, DebugHeal, DebugMaxSkills, DebugDamage, DebugGiveItem, DebugSpawn (Debug) |
| 240 | StartContract (H) |

There are 79 concrete records. The abstract `SimCommand`, `DebugCommand` and `SquadCommand` get no tag.

**Layout.** Varint tag, then the constructor parameters in declaration order, then the init-only properties in declaration order. The init-only properties are:
- `MoveOrder.Queued`
- `AttackOrder.Defensive`
- `EditCharacter.Surgeon` and `.Paid`
- `BuyItem.Section`, `.X`, `.Y` and `.Equip`
- `SellItem.X` (int?) and `.Y`

**Enums:**
- `CharacterOrders`: varint;
- `SquadAi`, `ShackleWork`, `EditKind` and `byte?`: byte;
- `TaskType`: zigzag.

**Nested types:**
- `InventoryItemToken`: Index, Record, Section, X, Y, Quantity, then **raw** f32 Quality. Exactness matters because tokens are compared by equality (`InventoryCommands.cs:59-60`).
- `PlannedBuilding`: Record, Position, Rotation, Floor, Parent?.
- `FcsRecord`: through `FcsRecordCodec`.

**Read rules:**
- `Tick` and `Player` are not on the wire. `Read` sets them in the initializer, e.g. `new MoveOrder(ids, t) { Tick = tick, Player = player, Queued = q }`.
- A HostOnly tag throws. A Debug tag throws unless `allowDebug`.
- Every float must be finite.
- The gate module does the semantic checks (ownership, quantities) and strips the trust fields `Defensive` and `Paid`. The codec carries every field.

**`CommandBatch`:** FirstSeq, then count ≤ 64, then the commands. Seqs are consecutive per client.

**Completeness test** (`CommandCodecTests`):
1. Reflect over `typeof(SimCommand).Assembly`:
   - every non-abstract subtype has exactly one tag;
   - tags are unique;
   - abstract types have none.
2. Make a sample of each type. Fill every constructor parameter and every public settable or init property with a non-default value from a sample table covering primitives, nullables, `CharacterId`, id lists, vectors, each enum's highest defined value, `InventoryItemToken`, `PlannedBuilding` lists, and an `FcsRecord` with every map filled. A type missing from the table fails the test and is named.
3. Round-trip with `tick=77, player=3`. Then:
   - deep-compare every public property (sequences element by element, `FcsRecord` field by field), excluding `Tick`, `Player` and `EqualityContract`;
   - assert every decoded property is non-default (this catches a forgotten init property);
   - check that Tick and Player were set.
4. HostOnly tags are refused, and Debug tags are refused unless allowed.

## 5. Snapshot core: `CharacterWire` (WP3a)

**What it is.** A `CharacterWire` holds the quantized fields of one `CharacterSnapshot`, with value equality per group.
- **Host:** quantizes each per-viewer snapshot into a `CharacterWire`.
- **Client:** keeps wires and rebuilds snapshots from them.

Each group is one bit of a varint mask, hottest first:

| Bit | Group | Fields → wire |
|---|---|---|
| 0 | Position | `Q64`×3. With a baseline: zigzag (q − q_base); without one: zigzag absolute |
| 1 | Yaw | `Yaw16` |
| 2 | Animations | see below |
| 3 | Flags | One varint of WeaponDrawn, IsPlayer, IsTrader, Selected, Inspected, PickingLock, WorkingShackles, InBed, InCombat, Shackled, ShacklesLocked, CanBeShackled, CarriesShackles. Then Task (byte), Orders (varint), RunSpeed (byte), PortraitSerial (varint) |
| 4 | Body | `BodyStatus?`: presence byte; Blood `U16[0,1]`; WorstPart `S12`; Hunger `U16[0,4]`; one varint of Unconscious, Dead, NeedsAid, Care (2 bits), MedicJob, RescueJob, NeedsSplint |
| 5 | Carry | CarriedBy, Carrying, AttachBone |
| 6 | Stealth | Stealth (0 null, else 1 + level); StealthKoChance (`float?`); Watchers (≤ 32: position as `Q64` relative to the character, plus a level byte) |
| 7 | Ragdoll | `BonePose?` (see below) |
| 8 | Look | Appearance → `LookId` varint (0 = null); Scale as f32 |
| 9 | Identity | Name, Faction, SquadId, SurgeryCost (`NullableInt`), Activity? |
| 10 | Prison | `PrisonView?`: Locked, Sentence (f32), HoursLeft as zigzag minutes (so it is not resent every tick), Bail (`NullableInt`) |
| 11 | Turret | `MannedTurret?`: Key, BuildingId, GunId, Position, Yaw |
| 12 | Jobs | `PermaJob` list (≤ 64): Type (zigzag), Site?, Saved (`Hand`: presence byte + 5 zigzag; absent for `Hand.Null`) |

- **Detail fields go on the reliable channel (§6), not here:** Skills, AllSkills, SkillXpMultipliers, Hud, Bounties, HealthParts, ItemGrid, Inventory, Path.
- **Coverage.** With `Id` in the record header, that accounts for all 48 `CharacterSnapshot` members.
- **Why Hunger and Blood are quantized:** so a slow drain is not resent every tick.

**Animations:**
- One header byte: the layer count (≤ 8).
- Per layer, a flags byte:

  | Bit | Meaning | What follows |
  |---|---|---|
  | b0 | Definition differs from the baseline's layer at the same index | `Definition+1` varint; 0 means a name string follows (the `AnimationLayers.For` fallback) |
  | b1 | Weight differs | u16 over [0,1] |
  | b2 | Length differs | raw f32 |
  | b3 | Time escape | raw f32 |
  | b4 | Weight escape | f32 instead of u16, for a weight outside [0,1] |

- Otherwise time is sent as a u16 phase `round(Time/Length·65535)` (when Length > 0 and 0 ≤ Time ≤ Length).
- **Why this precision:**
  - audio reads `Time/Length` (`GameHost.AudioCharacters.cs:97`), so footstep fractions stay exact to 1/65536;
  - the 16-bit weights keep `CharacterSounds.cs:314`'s choice of dominant layer.
- **Client rebuild:**
  - `Name` = `lib.Definitions[Definition].Name`, as `AnimationSystem.Publish` sets it;
  - `Length` comes from the wire.
- A steady layer costs 3 B.

**Ragdoll:**
- presence byte;
- boneCount (≤ 512);
- the `Set` bitset;
- Weight as `U16[0,1]`;
- a pose flags byte (b0: positions as f32);
- per set bone: a smallest-three rotation, then the position as i16×3 at 1/256 unit. Any |component| ≥ 127 switches the whole pose to f32 (large animals).

Every set position is sent. `PoseScratch` reads positions only for root bones (`SkeletonRig.cs:313`), but the codec cannot see the rig. A frozen ragdoll costs 0 B once acked.

**Projectiles:**
- keyed by Id, with the same baselines as characters;
- `Q64` position deltas;
- unit direction;
- Stretch as f32;
- Stuck bit;
- Mesh string, sent only when the projectile is new.

```csharp
public sealed class CharacterWire
{
    static CharacterWire Quantize(CharacterSnapshot c, LookRegistry looks);                                  // host
    CharacterSnapshot ToSnapshot(CharacterId id, ILookResolver looks, AnimationLibrary lib, CharacterDetail? d); // client
    static readonly CharacterWire Default;  // Faction -1, SquadId -1, RunSpeed Free, Scale 1, ids None; the baseline of a new entity
    uint DiffMask(CharacterWire other);
}
// CharacterWireCodec.WriteGroups(w, s, now, baseline, mask) / ReadGroups(ref r, s, baseline, mask, lib)
```

**Invariant:** `Quantize(ToSnapshot(w))` equals `w` in every group.

## 6. Reliable views (WP3b, WP3c)

**`ViewUpdate` layout:** kind byte, ViewSeq (per client, +1 per message), Tick, then the body. Each message stays under 16 KB; bigger updates are split.

**Commit rule:** the client applies a view when it assembles a snapshot with `Tick ≥ view.Tick`. Views therefore change in step with the interpolated characters (a door and the character walking through it). While the game is paused the tick does not move, so views apply at once.

```csharp
public readonly record struct ItemCodec<T>(ItemWriter<T> Write, ItemReader<T> Read);   // delegates over (NetWriter, StringTableWriter) / (ref NetReader, StringTableReader)
public sealed class KeyedViewSync<TKey, TItem>(ViewKind kind, Func<TItem, TKey> key, ItemCodec<TItem> codec, TimeSpan minInterval) where TKey : notnull
{   // keeps, per key, the last encoded bytes, and the last key order
    bool Encode(IReadOnlyList<TItem> now, bool structural, TimeSpan time, NetWriter w, StringTableWriter s);
}
```

**Keyed body:**
1. A flags byte; b0 = the order changed.
2. If b0: the full key list. A key missing from it is removed.
3. The upserts: a count, then (key, item) pairs.

**Change detection:**
1. Skip when the source reference is unchanged. The lazy caches return the same object until their version moves: `BuildingRegistry.Published`, `GroundItems.Published`, `PublishedDoors`, `Machines.Published`, `ResearchView`, `CraftsView`.
2. Otherwise encode each item and compare it with the bytes last sent.
3. Item changes wait for `minInterval`. Membership changes and revision bumps (`structural`) go at once.

**Client side:** keeps the previous list reference while nothing changed, because host code diffs by reference and revision (`GameHost.Building.cs:28-41`).

| Kind | WorldSnapshot fields | Shape | Key | Min interval |
|---|---|---|---|---|
| 1 Header | SquadAi, ActiveSquad, plus fields other modules add (money, faction…) | single | | 0 |
| 2 Towns | Towns | byte[] | | 1 s |
| 3 Camps | Camps | keyed | Site | 1 s |
| 4 Squads | PlayerSquads | keyed | Id | 0 |
| 5 Buildings | Buildings + BuildingsRevision (structural) | keyed | Id | 200 ms |
| 6 GroundItems | GroundItems + GroundItemsRevision | keyed | Id | 0 |
| 7 Containers | Containers | keyed | Id | 1 s |
| 8 DoorPicks | DoorPicks | keyed | Id | 1 s |
| 9 Doors | Doors | keyed | (Building, Door) | 0 |
| 10 Unlocked | UnlockedBuildings | keyed string set | | 0 |
| 11 Inspected | InspectedBuilding | single | | 0 |
| 12 Machines | Machines + MachinesVersion | keyed | Site | 200 ms |
| 13 Research | Research | header + keyed `Available` | Id | 200 ms |
| 14 Crafts | Crafts | keyed | site → list | 500 ms |
| 15 Trade | Trade | single, nullable | | 0 |
| 16 Conversation | Conversation | single | | 0 |
| 17 DialogueRequest | DialogueRequest | single | | 0 |
| 18 Speech | Speech (speakers in interest) | keyed | Speaker | 0 |
| 19–22 Detail | Stats (Skills, AllSkills, SkillXpMultipliers, Hud, Bounties) 100 ms; Health (HealthParts) 100 ms; Inventory (ItemGrid, Inventory) 0; Path 250 ms | keyed | CharacterId | as listed |

`Tick`, `Characters` and `Projectiles` go in the parts, so all 25 `WorldSnapshot` members are covered.

**Item encodings.** Fields go in declaration order: positional first, then init properties. The general rules:
- floats and doubles are raw (these are UI values at low rates);
- strings are interned;
- free text is sent as a literal: Conversation Text, Choices and Log, and `ResearchOption.Description`;
- bools are packed;
- lists are a count (≤ MaxListItems) followed by the items.

The fields to cover:
- **`TradeView`:** Customer, Trader, TraderName, ShopMoney, PlayerMoney, Stock size, Stock[], Goods[], BuysStolen.
  - **`TradeItemView`:** its 13 positional fields, then Material, Charge?, Value, SellValue, Weight, FenceChance?, FenceOdds?.
- **`ConversationView`:** the ids, names, Text, Choices, PlayerMoney. The Log is sent as `kept` (how many leading lines equal the last log sent), then only the new lines.
- **`DialogueRequest`:** Id, Kind, Player, Npc.
- **`SpeechBubble`:** Speaker, Text (interned, since lines repeat), Large.
- **`CampView`:** 7 fields; Position is a `Vector3?`.
- **`SquadView`:** Id, Name, Members.
- **`BuildingSnapshot`:** its 12 fields (Materials as `MaterialView`), then Dismantling, WallParent.
- **`GroundItemSnapshot`**, **`ContainerSpot`**, **`DoorPick`**: as declared.
- **`DoorView`:** Open (f32), Motion, Locked, WantsLock, Broken, Position, Record.
- **`BuildingDetail`:** Instance, then per door Open, Locked, Level, Broken.
- **`MachineSnapshot`:** Site, Record, Inputs (`MachineSlotView`), Output?, Storage?, Farm? (8 fields plus PlantGrowth), Status, Operators, MaxOperators, IsContainer, TechLevel, Operable, IsStorage, Craft? (Queue, Repeat, Done), Power? (Kind, 5 floats, Fueled), Sound.
- **`ResearchSnapshot`:** a header (TechLevel, BenchLevel, Queue, Rate, Finished) plus `ResearchOption` items keyed by Id (8 positional fields, Description, Cost). An empty decode returns `ResearchSnapshot.Empty`.
- **`CraftOptionView`:** 7 fields.
- **`InventoryView`:** 4 ints, Items (`InventoryItemView`: 11 fields, Material, Charge?), CarryCapacity?, TotalWeight.
- **`HudView`:** 4 floats, then `HudStatLine` items, then `ExtendedStats?`.
- **`HealthPartView`:** 8 fields.
- **`BountyLine`:** HoursLeft is an f64 and may be +∞.
- **`SkillSummary`:** 6 floats.

**Looks** (WP3c):
- **Recording.** The host wraps its appearance source in `RecordingAppearances : IAppearanceSource`. The wrapper forwards every call and keeps the arguments of each `Create` in a `ConditionalWeakTable<CharacterAppearance, AppearanceRecipe>`.
  - Every look in play is made through `IAppearanceSource`: `PopulationSystem.Build.cs:140`, `CharacterAssembly.RefreshEquipment`, `CharacterEditing.Apply`, `SavedPopulation.cs:145-156`. Replaying the recipe therefore repeats the exact factory call.
  - The wrapper is installed on the `PopulationData` / `SaveLoader` options only when hosting.

```csharp
public abstract record AppearanceRecipe
{
    sealed record Rolled(string RecordId, string? FactionId, int Seed);   // Create(recordId, factionId, seed); animals too
    sealed record Kit(string RecordId, LoadoutWire Loadout);              // Create(recordId, loadout)
    static AppearanceRecipe? FromLook(CharacterAppearance look);          // fallback: Kit from look.Loadout, else Rolled(look.Animal.StringId, null, 0)
    CharacterAppearance? Build(IAppearanceSource source, GameDatabase db);
}
```

- **`LoadoutWire`** carries the `Loadout` with records as string ids:
  - Seed, Character?, Race, Female;
  - Appearance (the full `FcsRecord`), Generated, MorphIndex;
  - Clothing, Backpack? and Carried, each as (Record, Quality, Material?);
  - Crossbow? and Weapons, each as (Weapon, Manufacturer?, Model?, Level, Section, Row);
  - `Limbs[4]` as (State, with 0 = natural; Prosthetic?).
  - `Notes` are dropped.
  - A record the client does not have gives a null look (marker).
- **`LookRegistry`** (host): `uint IdOf(CharacterAppearance?)`.
  - 0 = null. Otherwise one stable id per object reference, so a species' shared animal look is sent once.
  - It also tracks which ids each client has already been sent.
- **`LookDefinition`** (`0x31`, ch2): id, recipe kind byte, recipe. It is sent before the first part that uses the id.
- **`LookCache : ILookResolver`** (client): `Define` builds the look on the ThreadPool (`Build` reads files), and `Resolve` returns null until it is ready. Until then the mirror keeps the character's last resolved look.

## 7. Delta, interest, packetizer (WP4)

**Interest** (per client, per send):
- **Always in:** every `IsPlayer` character (co-op squad bars), plus the characters the client's UI refers to:
  - Selected and Inspected;
  - Trade customer and trader;
  - Conversation ids;
  - DialogueRequest ids;
  - PlayerSquads members.
- **Nearby:** characters within `R = min(view.DrawDistance, 8000) + 300` of Eye on X/Z. A character leaves only beyond R+300.
  - The renderer's default `DrawDistance` is 6000 (`CharacterRenderer.DrawDistance`).
- **Closure:** one step over CarriedBy / Carrying.
- **Weights:**
  - **1:** closer than 1500, UI references, and the viewer's own characters (when ownership adds `Owner`);
  - **0.5:** closer than 3500;
  - **0.25:** beyond.

```csharp
public sealed record SnapshotEncoderOptions { int SendHz = 20; int BytesPerSecond = 96_000; int BaselineWindow = 60; int History = 128; int KeepAliveHz = 5; }
```

**`SnapshotEncoder` state** (internal, one per client):
- `acked`: CharacterId → (Seq, wire), and the same for projectiles;
- the priority accumulators;
- pending removals;
- a ring of the last `History` parts sent, each with its Seq, part number, entities, string definitions and removals.

**Encoding a send:**
1. **Quantize** the interest set. If several viewers share one `CharacterSnapshot`, its wire may be cached in a `ConditionalWeakTable`.
2. **Pick a baseline.** Use the acked state if `Seq − acked.Seq ≤ BaselineWindow`, else none. Compute `mask = DiffMask`. If the mask is 0, skip the entity and reset its accumulator.
3. **Order and budget.**
   - First: new entities, Resync slots and removals.
   - Then: the rest by accumulated weight `acc`, highest first.
   - Stop when this send's budget (`BytesPerSecond / SendHz`) is spent. A client that just joined gets double budget for its first 2 s.
   - Sending an entity resets its `acc`.
4. **Packetize.**
   - Encode each entity under a string-table mark.
   - If it would exceed `sink.MaxUnreliablePayload`: `Truncate`, `Rollback(mark)`, flush the part, start a new one, and encode again.
   - An entity bigger than a whole part drops its Ragdoll group for that send (logged once).
   - At most 64 parts.
   - If there is nothing to send, still send a header-only part at `KeepAliveHz`.

**Part layout:**
```
0x10 | Seq | Tick | GameHours f64 | flags (b0 paused, b1-3 speed) | ViewSeq | PartIndex byte | PartCount byte
| removals: count, (slot, generation)… | projectiles: count, records | characters: count, records
record = slot (zigzag gap from previous) | length varint | back (0 = none → generation follows; else baseline Seq = Seq - back) | mask | groups
```

The length prefix lets a client skip a record it cannot decode (an unknown baseline) and put that slot in Resync.

**Acks** (from `ClientFrame`). For each (Seq, part) still in the ring that has not been processed yet:
1. For every entity in that part with `acked.Seq < Seq`, set `acked` to (Seq, wire). Baselines therefore only ever move forward.
2. Call `Confirm` on the part's string definitions.
3. Drop `acked` for every acked removal.

A pending removal is resent in part 0 until it is acked. If the entity comes back first, the removal is cancelled and a full record is sent.

```csharp
public sealed class ClientReplicator(int player, SnapshotEncoderOptions options, LookRegistry looks, AnimationLibrary animations)
{
    void Send(WorldSnapshot view, SessionClockState clock, TimeSpan now, IPacketSink sink);  // interest → looks → views (ch2) → parts (ch0)
    void OnClientFrame(ClientFrame frame);
    IReadOnlySet<CharacterId> Interest { get; }  ViewState View { get; }
}
public readonly record struct SessionClockState(bool Paused, byte SpeedIndex, double GameHours);

public sealed class ClientMirror(AnimationLibrary animations, ILookResolver looks)
{
    void Receive(byte channel, ReadOnlySpan<byte> packet);   // ch0 error: drop the part; ch1-3 error: throws (the caller disconnects)
    bool TryAssemble(out WorldSnapshot snapshot);            // when the newest Seq or the committed views changed
    long TickOf(CharacterId id);                             // tick of this entity's newest state, for per-entity interpolation
    SessionClockState Clock { get; }
    void WriteFrame(NetWriter w, ViewState view);
    bool TryTakeEvents(out EventBatch batch);
}
public interface ILookResolver { CharacterAppearance? Resolve(uint lookId); void Define(uint lookId, AppearanceRecipe recipe); }
```

**Mirror rules:**
- **Storage:** a ring per entity of (Seq, wire) covering the last 64 Seqs. 64 is more than `BaselineWindow`, so any baseline the host acks is still there.
- **Pruning:** when a record names baseline B, states older than B are dropped.
- **Ordering:** the highest Seq wins.
- **Removals:** apply only when their Seq is at least the entity's newest Seq.
- **Slot reuse:** a new generation replaces the entity.
- **Assemble:** builds `CharacterSnapshot`s ordered by slot, as `World.Publish` does, and merges the detail views.

## 8. Events (WP5)

`EventBatch` (`0x40`, channel 3): Tick, then count (≤ 1024), then the events. Each event is a kind byte followed by its fields:

| Kind | Type | Fields |
|---|---|---|
| 1 | `SoundEvent` | SoundKind, Subject, Other, Position (`Q64`), flags (Heavy, AttackerUnarmed, AttackerRobot, Armoured, Fatal, hasItem), Item |
| 2 | `BloodMark` | Kind, Who, Position (`Q64`), Direction (2× Half), Amount (Half), Colour (3 B), Race? |
| 3 | `Floater` | Kind, Target, Position (`Q64`), flags (TargetIsPlayer, SourceIsPlayer), Part, `HitDamage` (5× f32), `ArmourAbsorbed` (3× f32), Healed |
| 4 | message `string` | literal text (≤ 1024 B) |
| 5 | `StealthKoEntry` | Tick, Attacker, Target, Chance, Success |
| 6 | `ProspectReport` | all 9 fields; each `ProspectLine` is Name and Value |
| 7 | new: `ChatLine(int From, string Text)` | From (byte), literal Text (≤ 512 B) |
| 8 | new: `Notice(NoticeKind, int Player, int Value)` | NoticeKind is Paused, Resumed, Speed, Joined, Left or Kicked |

```csharp
public abstract record NetEvent;  // SoundNet, BloodNet, FloaterNet, MessageNet, KnockoutNet, ProspectNet, ChatNet, NoticeNet
public sealed record EventBatch(long Tick, IReadOnlyList<NetEvent> Events);
public static class EventFilter { static bool Wants(in NetEvent e, ViewState view, IReadOnlySet<CharacterId> interest, float radius); }
```

- **Draining.** The host empties the Sounds, BloodMarks and Floaters queues once per frame, as it does today.
- **Kinds 1–3 (spatial).** A client gets the event when its Position lies within the radius of Eye, or when its subject, other, who or target is in that client's interest set.
- **Kinds 4–8.** The host modules choose who receives each one.
- **Sending.** One batch per client per frame, and only when it is non-empty.

## 9. Estimate: 300 visible characters at 20 Hz

**Per character:**
- **Record overhead:** about 4.5 B.
- **Position:** the baseline is about 3 sends old (the last acked one), so the delta is about 2 B per horizontal axis.
- **Cost by state:**

  | State | B/send |
  |---|---|
  | Idle | 9–12 (animation only) |
  | Walking | about 18 |
  | Fighting | about 30 |
  | Active ragdoll (20 bodies) | about 220 |
  | Frozen ragdoll | 0 |

**Two scenes:**

| Scene | Mix | Per send | Raw rate | With distance weights |
|---|---|---|---|---|
| Town | 180 idle, 100 walking, 20 fighting | 4.2 KB (4 parts) | about 86 KB/s (0.69 Mbit/s) | about 50 KB/s |
| Battle | 150 idle, 35 walking, 100 fighting, 15 ragdolls | 8.4 KB | about 168 KB/s | about 98 KB/s, then held to the 96 KB/s budget by priority |

**Joining:**
- characters: about 70 B each, so 21 KB;
- looks:
  - a Rolled recipe is 10–30 B;
  - a Kit recipe (saves and edited characters) is about 450 B, since its slider keys are interned;
  - up to about 135 KB in all;
- views: research and doors are tens of KB each.

The whole join is about 250–400 KB.

**Steady state:**
- views: a few KB/s;
- events: about 5 KB/s in a large fight;
- upstream: about 1 KB/s.

All of these are estimates; real counts have not been measured. The WP4 tests should log the bytes they see.

## 10. Invariants, edge cases, leave alone

- **Quantization.** The host compares quantized values. The client draws exactly those values, which are within 2 mm of the host's raw floats.
- **Looks.** `CharacterSnapshot.Appearance` is never sent: the client rebuilds it from the LookId. A null look still shows a marker.
- **Delta baselines.** The host never deltas against a state that is unacked or older than `BaselineWindow`.
- **Strings on channel 0.** They are defined again in each part until acked. Volatile text (dialogue, messages, chat) is never interned.
- **Paused-game edits.** These are commands applied between ticks. They republish at the same Tick: `Seq` still advances and the views commit at once.
- **Speed.** At 5× the tick moves about 7 per send. Interpolation goes by tick difference, using `TickOf`.
- **Host-side reads.** Anything a client sends that is malformed throws. This includes NaN or infinite floats, unknown or HostOnly tags, over-limit counts, bad UTF-8, trailing bytes and duplicate FCS keys. The client reads host data with the same limits.
- **Leave alone:**
  - the shapes of `WorldSnapshot` and `CharacterSnapshot` (detail views and LookIds exist only on the wire);
  - the simulation, `FcsWriter`, `GameHost` and single-player paths;
  - `RecordingAppearances` is used by the multiplayer host only.

## 11. Cross-module interfaces

**Exposes:**
- `NetWriter`, `NetReader`, `NetFormatException`, `NetLimits`, `StringTableWriter` / `StringTableReader`, `MessageType`, `Channels`, `IPacketSink`;
- `Hello`, `Welcome`, `ClientFrame` and `ViewState`, with their codecs. The session module frames its `Control` bodies with these primitives.
- `CommandCodec.Write(NetWriter, StringTableWriter, SimCommand)` and `CommandCodec.Read(ref NetReader, StringTableReader, long tick, int player, bool allowDebug)`;
- `CommandTags.All`, `CommandOrigin`, `CommandBatch(uint FirstSeq, IReadOnlyList<SimCommand>)`, `CommandResult(uint Seq, bool Accepted, string Reason)`;
- `ClientReplicator`, `SessionClockState`, `SnapshotEncoderOptions`, `LookRegistry`, `RecordingAppearances(IAppearanceSource inner)`;
- `ClientMirror`, `ILookResolver`, `LookCache(IAppearanceSource, GameDatabase)`, `AppearanceRecipe`;
- `EventBatch`, `NetEvent` and its subtypes, `ChatLine`, `Notice`, `EventCodec`, `EventFilter.Wants`;
- `FcsRecordCodec` and `Quant`.

**Consumes:**
- **Ownership module:**
  - `public int Player { get; init; }` on `SimCommand`. If it ends up named differently, rename it in `CommandCodec.Read`.
  - Optionally an `int Owner` on `CharacterSnapshot`, used for the interest weights.
- **Per-viewer publish module:**
  - One `WorldSnapshot` per player per send.
  - Any field it adds (money, faction, relations, owner…) needs a codec group or a view. The coverage test fails until one exists.
- **Session/transport module:**
  - An `IPacketSink` per peer: `NetPeer.Send(ReadOnlySpan<byte>, byte channel, DeliveryMethod)`, with `MaxUnreliablePayload = Math.Min(1200, peer.GetMaxSinglePacketSize(DeliveryMethod.Sequenced))`.
  - Routing of `(channel, bytes)`.
  - `DataHash` and the player number.
  - A `SessionClockState` for each send (pause, speed, `World.GameHours`).
- **Host loop:**
  - Drains the world's queues, `World.TakeMessages()`, `CarrySystem.TakeKnockouts()` and `World.Prospected` (a changed Serial means a new report), each with its recipients.
  - Calls `ClientReplicator.Send` at `SendHz`.
- **Presenter module:**
  - Uses `ClientMirror.TryAssemble`, `TickOf`, `Clock` and `TryTakeEvents`.
  - Passes the `ViewState` to `WriteFrame`.
- **Data, on the client:**
  - `AnimationLibrary.FromDatabase(db)`;
  - `GeneratedAppearances(db, installRoot)`.

## 12. Tests (`tests/Meitou.Tests/Net/`, approved)

1. **Primitives:**
   - varint and zigzag edge values;
   - raw NaN payloads and -0;
   - every `NetReader` failure;
   - string table definitions, references, literals, rollback, and redefinition until confirmed.
2. **Quant:**
   - positions at ±HalfWorldSize within 1/128;
   - yaw wraparound;
   - smallest-three error below 0.003 rad;
   - phase precision of 1/65535 of `Time/Length`;
   - `Quantize∘ToSnapshot` idempotent.
3. **`FcsRecordCodec`:** full round-trip, and duplicate keys refused.
4. **Command completeness and round-trip** (§4).
5. **Wire coverage.** Each codec lists `static readonly string[] Covered = [nameof(…)]`. Reflection over the public properties of `CharacterSnapshot`, `WorldSnapshot` and every nested type in §5–8 must find each property claimed.
6. **Replication.** A memory sink with seeded drops, reorders and duplicates feeds synthetic snapshots.
   - After a quiet period, the mirror must equal the host's quantized view and its views.
   - Removals, slot reuse, string loss and budget starvation must recover.
   - Bytes per send are logged.
7. **Events:** every kind round-trips, and the spatial filter works.
8. **[Slow], needs the game:**
   - two `AnimationLibrary` builds give the same `AnimationHash`;
   - Rolled and Kit recipes rebuild the same part records and meshes as the host.

## 13. Work packages

| WP | Content | Files | After |
|---|---|---|---|
| WP1 | csproj, slnx, notices, primitives, Quant, StringTable, CommonCodec, FcsRecordCodec, framing, `docs/net.md` skeleton, tests 1–3 | `Primitives/`, `Fcs/`, `Messages/` | – |
| WP2 | command table, codec, batch, test 4 | `Commands/` | WP1 |
| WP3a | CharacterWire, animation, ragdoll and projectile codecs | `Snapshot/*.cs` | WP1 |
| WP3b | KeyedViewSync; World and Production views (kinds 5–14) | `Views/KeyedViewSync`, `ViewCodecs.World`, `ViewCodecs.Production` | WP1 |
| WP3c | Player and Detail views (kinds 1–4, 15–22), looks, test 8 | `Views/ViewCodecs.Player`, `ViewCodecs.Detail`, `Looks/` | WP1 (it stubs KeyedViewSync to the §6 signature) |
| WP4 | interest, encoders, ClientReplicator, ClientMirror, tests 5–6 | `Delta/` | WP3a–c |
| WP5 | events, filter, handshake bodies, test 7 | `Events/`, `Messages/Handshake` | WP1 |

- **Parallel work:** after WP1, packages WP2, WP3a, WP3b, WP3c and WP5 touch disjoint files and can run in parallel.
- **Docs:** each package adds its own section to `docs/net.md`, in the same change.
