## Command serializability for multiplayer (read-only research)

### Base contract
- `public abstract record SimCommand { public required long Tick { get; init; } }` (src/Meitou.Simulation/Commands.cs:10-14). There is no issuer or player field.
- There are **79 concrete sealed records** (a grep count that I checked) and 3 abstract ones: `SimCommand`, `DebugCommand` (DebugCommands.cs:9) and `SquadCommand` (SquadManagement.cs:8). There are none in tests.
- **Ordering:** `CommandQueue.TakeDue` sorts by Tick and then by a local `sequence` counter (Commands.cs:64). That sequence is the order commands were enqueued on one machine. With several peers you need a canonical tie-break, for example (tick, playerId, per-player seq).
- **Internal commands:** `World.Defer` adds commands that are "world state, not the host's: they never go through Commands" (World.cs:236-245). Deferred types are `AttackOrder` (including `Defensive = true`, Fighting.cs:92), `StopCommand`, `MoveOrder` and `OpenTrade`. Call sites include AggroSystem.cs:392/463, DialogueSystem.cs:171/179, TradeSystem.cs:131/136, FleeSystem.cs:65/117 and ArrestSystem.cs:302. These must never go on the wire, and clients must not be allowed to forge their flags.

### CharacterId and other IDs
- `public readonly record struct CharacterId(int Slot, int Generation)`, with `None = new(-1, 0)` (CharacterId.cs:7-9). It is two int32s and serializes trivially.
- **Slots come from a free stack:** `slot = free.Pop(); generation = next[slot].Generation + 1`, or highWater (CharacterTable.cs:340-341). IDs match across peers only if spawns and despawns happen in the same order at the same ticks. Two things in the code break that:
  - Town residents "are built on a background thread when `PopulationSettings.Background` is set and taken in at the first tick after they are ready" (PopulationSystem.cs:21, worker thread :69-71, drained :107).
  - `PathService`: "timing can differ between runs" unless running synchronously (PathService.cs:12-15; `SynchronousPaths` is tests only, StandardSystems.cs:52).
  
  **Observed:** under (L), CharacterIds are not portable between peers unless both are forced synchronous. Under (S), host-assigned IDs are authoritative, so this is fine.
- **Squad ids:** a counter, `NextId() => nextId++` (Squads.cs:75-81). Same caveat.
- **Ground item ids:** `GroundItems.nextId++` (GroundItems.cs:32,42). Same caveat.
- **Building instance strings:** town buildings come from the game data, so they are stable when data and mods match. Player sites are `<n>-zone<X>.<Y>-INGAME` with a per-zone counter (BuildingRegistry.cs:132,141,205-213), which is deterministic if `PlaceBuildings` is applied in the same order everywhere.
- **Fixture ids** are derived: the instance id, `<instance>#<i>` for doors, `<instance>@bed<k>`, or `TownFixture.Id` (Fixtures.cs:61-63).
- **Record ids** (item, research, BUILDING, package, character record) are FCS `StringId`s. They are stable only with an identical mod set and load order, so you need a data and mod hash in the join handshake. `CraftOption.Key => Model is null ? Item : Item + "|" + Model` (CraftingData.cs:15).
- **Sections** are a closed vocabulary: `main`, `backpack_content`, `back`, `hip`, `shirt`, `head`, `legs`, `boots`, `armour`, `belt`, `backpack_attach` and limb sections (InventoryCommands.cs:10). They could be coded as a byte enum.
- **`InventoryItemToken(int Index, string Record, string Section, int X, int Y, int Quantity, float Quality)`** (InventoryCommands.cs:4):
  - `Index` is a position in `inventory.All()`.
  - The token is checked by exact equality, including `item.Quality != token.Quality` (InventoryCommands.cs:59-60). The float codec must therefore be bit-exact; use binary float32, not decimal text.

### Table
**Field abbreviations:**
- `L` = `IReadOnlyList<CharacterId>`
- `C` = `CharacterId`
- `s` = string
- `V2`, `V3`, `Q` = `Vector2`, `Vector3`, `Quaternion`
- `Tok` = `InventoryItemToken`
- `+x` = an init property outside the constructor

**Implicit-player codes:**
- **Sel**: an empty list means `world.Player.Selection`.
- **P**: uses world.Player's faction, money, research or other state, or `IsPlayer`.
- **1**: a single per-world UI or session slot.
- **!**: I found no ownership check in the handler.
- **—**: none.

**Kind codes:**
- **G**: gameplay.
- **UI**: display only, not hashed.
- **St**: hashed selection or squad-bar state.
- **H**: sent by the host or engine.
- **T**: test or sandbox.
- **D**: debug cheat.

| Command | File:line | Fields | Implicit | Kind |
|---|---|---|---|---|
| MoveOrder | Commands.cs:17 | L, V3 Target, +bool Queued | Sel (MovementSystem.cs:111); ! no IsPlayer check (:113-117) | G |
| SandboxMovement | Commands.cs:24 | C, byte? Mode, float? Speed | ! | T |
| FocusCommand | Commands.cs:75 | V3 | one global `focus` (PopulationSystem.cs:102) drives zone activation and spawning | G (camera-driven) |
| SelectCommand | Commands.cs:78 | L, bool Additive | P (IsPlayer filter, player.Selection, player.Squad) | St |
| InspectCommand | Commands.cs:81 | C | P (player.Inspected) | UI |
| InspectBuildingCommand | Commands.cs:84 | s? Instance | P | UI |
| SeenDiscoveriesCommand | Commands.cs:87 | — | P (player.Towns, "Not hashed") | UI |
| StopCommand | Commands.cs:90 | L | Sel (PlayerState.cs:157) | G |
| RepathCommand | Commands.cs:96 | — | — | H (GameHost.cs:325, after Stopwatch-timed navmesh loads) |
| SquadAiCommand | PlayerState.cs:278 | SquadAi:byte flags, bool | P (player.SquadAi) | G |
| OrdersCommand | Orders.cs:53 | L, CharacterOrders:ushort, bool? | Sel, P (Orders.cs:165-167) | G |
| RunSpeedCommand | Orders.cs:59 | L, int Step | Sel, P | G |
| EditCharacter | Editing/CharacterEditing.cs:16 | C, **FcsRecord Appearance**, s Name, EditKind:byte, +C Surgeon, +bool Paid | P (Money, InCreation) | G |
| FinishCreation | CharacterEditing.cs:25 | — | P | G |
| TalkOrder | Dialogue/DialogueSystem.cs:7 | C Talker, C Target | 1 (talker/target fields :81) | G |
| DialogueChoice | DialogueSystem.cs:9 | int Index | 1 (`Conversation? open` :83) | G |
| EndConversation | DialogueSystem.cs:11 | — | 1 | G |
| DebugMoney | DebugCommands.cs:12 | long | P | D |
| DebugTeleport | :15 | L, V3 | ! | D |
| DebugHeal | :18 | L | ! | D |
| DebugMaxSkills | :21 | L | ! | D |
| DebugDamage | :24 | L, float Cut | ! | D |
| DebugGiveItem | :27 | C, s Record, int | ! (static `ItemTypes` is not a field) | D |
| DebugSpawn | :35 | s Record, V2 At | P (faction hostile to the player) | D |
| ProspectOrder | Production/Prospecting.cs:9 | C | host picks the character nearest the camera | G |
| PutShacklesOrder | Locks/LockSystem.Shackles.cs:15 | L, C Wearer | Sel (LockSystem.cs:26) | G |
| ShackleOrder | Shackles.cs:18 | L, C, ShackleWork:byte | Sel | G |
| OpenTrade | Trading/TradeSystem.cs:8 | C Customer, C Trader | 1 (customer/trader/open :98-99), P | G (also deferred) |
| CloseTrade | :10 | — | 1 | G |
| BuyItem | :15 | Tok (stock index), int, +s? Section, +int X, +int Y, +bool Equip | 1, P money | G |
| SellItem | :25 | Tok, int, +int? X, +int Y | 1, P | G |
| ArrangeStock | :54 | — | 1 | G |
| ToggleDoorsOrder | Locks/LockSystem.Doors.cs:6 | s Building | P (PlayerDoors) | G |
| ToggleDoorLocksOrder | Doors.cs:9 | s Building | P | G |
| DoorOrder | Doors.cs:16 | L, s Door (fixture id), bool PickLock | Sel; 1 (DoorTarget/DoorWalker, PlayerState.cs:36-37) | G |
| PickLockOrder | Locks/LockSystem.cs:8 | L, C | Sel | G |
| GetOutOrder | LockSystem.cs:11 | L | Sel | G |
| NewSquadCommand | SquadManagement.cs:11 | V3 | P | G |
| RenameSquadCommand | :14 | int, s | P | G |
| RemoveSquadCommand | :17 | int | P | G |
| MoveToSquadCommand | :23 | C, int Squad, int Index, bool Swap | P | G |
| ReorderSquadCommand | :26 | int, int | P | St |
| ActiveSquadCommand | :29 | int | P (player.Squad, hashed; recruits join it) | St |
| DismissCommand | :32 | C | P | G |
| AttackOrder | Combat/CombatTypes.cs:10 | L Attackers, C Target, +bool Defensive | ! no fallback and no owner check (CombatSystem.cs:133); `Defensive` skips assault and relation (:131,138) | G (also deferred) |
| OperateOrder | Production/ProductionCommands.cs:6 | L, s Site | Sel (ProductionSystem.cs:160) | G |
| AddJob | :12 | L, TaskType (int enum, ~290 values), s Site | Sel | G |
| RemoveJob | :15 | C, int | not verified | G |
| MoveJob | :18 | C, int, int | not verified | G |
| PutInMachine | :24 | C, Tok, s Site, int Slot, int X, int Y | P (player building) | G |
| TakeFromMachine | :30 | C, s Site, int, int, s Section | P | G |
| QueueResearch | :33 | s Research | P (one global `world.Research`) | G |
| CancelResearch | :36 | s | P | G |
| QueueCraft | :39 | s Site, s Option (Key), int | P | G |
| CancelCraft | :42 | s, int | P | G |
| RepeatCrafts | :45 | s, bool | P | G |
| PayBailOrder | Crime/CrimeTypes.cs:185 | C Prisoner | P ("a free player character near the cage pays") | G |
| PutInCageOrder | CrimeTypes.cs:188 | L Carriers | Sel (ArrestSystem.Bounties.cs:57) | G |
| PickUpItemOrder | Items/PickUp.cs:6 | L, int Item (ground id) | Sel (:25) | G |
| OpenContainerOrder | Items/Containers.cs:6 | L, s Fixture | Sel; 1 (OpenContainer etc., PlayerState.cs:31-34) | G and UI |
| CloseContainer | Containers.cs:9 | — | 1 | UI |
| PickUpOrder | Carrying/CarryTypes.cs:7 | L, C | Sel (CarrySystem.cs:107) | G |
| PutDownOrder | :10 | L | Sel | G |
| LootOrder | :16 | L, C | Sel | G |
| StealthKoOrder | :22 | L, C | Sel | G |
| RescueJobCommand | :32 | L, bool Job | Sel | G |
| ReadMapItem | Items/MapItems.cs:7 | C, Tok | P (player.Towns) | G (map knowledge) |
| PlaceBuildings | Construction/ConstructionCommands.cs:9 | List of `PlannedBuilding(s Record, V3, Q, int Floor, s? Parent)` (:6) | P (`Owner = world.Player.Faction`, ConstructionSystem.cs:231) | G |
| BuildOrder | :12 | L, s Site, bool Repair | Sel (ConstructionSystem.cs:131) | G |
| DismantleOrder | :15 | L, s | Sel | G |
| CancelSite | :18 | s | P | G |
| PauseSite | :21 | s, bool | P | G |
| MoveInventoryItem | Items/InventoryCommands.cs:5 | C, Tok, s Section, int X, int Y | P (IsPlayer, PlayerState.cs:167) | G |
| ArrangeInventory | :6 | C | P (:200) | G |
| TreatOrder | FirstAid/FirstAidTypes.cs:8 | L, C | Sel (FirstAidSystem.cs:88) | G |
| SplintOrder | :14 | L, C | Sel | G |
| RestOrder | :17 | L | Sel | G |
| MedicJobCommand | :23 | L, bool | Sel | G |
| StartContract | Ai/AiSystem.cs:36 | int SquadId, s PackageId, float Hours | ! (any squad, :578) | T (no UI sender) |

### What a binary serializer needs
1. **Type tag registry:** a stable ushort per concrete type, as an explicit table rather than reflection order, so it survives refactors. It must cover the 79 concrete types and exclude engine-only ones (`RepathCommand`, and `SandboxMovement`/`StartContract` unless enabled). Add a protocol version and a data-set hash.
2. **Header:** `Tick` (varint delta), issuer `PlayerId` (new) and a per-player sequence.
3. **Primitive codecs:**
   - int, long, float32 (raw bits), bool, byte
   - nullable wrappers: `byte?`, `float?`, `bool?`, `int?`, `string?`
   - UTF-8 length-prefixed strings, ideally interned into a per-session string table for record, site and section ids
   - `CharacterId` as 2×varint
   - `IReadOnlyList<CharacterId>` as count plus items
   - `Vector2`, `Vector3`, `Quaternion` as float32×n
   - enums at their underlying width: `CharacterOrders` ushort; `SquadAi`, `ShackleWork`, `EditKind` byte; `TaskType` int
4. **Nested codecs:**
   - `InventoryItemToken` (7 fields)
   - `PlannedBuilding` (5 fields, `Parent` nullable)
   - `FcsRecord`, which is the only object-reference field. It is a mutable class with `OrderedDictionary` maps of bools, floats, ints, Vector3s, Vector4s, strings and filenames, plus `References` lists and `Instances` (Data/Fcs/FcsRecord.cs:53-67). `FcsWriter` works on whole `FcsFile`s only (FcsWriter.cs:18), so it needs its own record-level codec.
5. **Init-only properties outside the constructor:**
   - `MoveOrder.Queued`
   - `AttackOrder.Defensive`
   - `EditCharacter.Surgeon` and `.Paid`
   - `BuyItem.Section`, `.X`, `.Y`, `.Equip`
   - `SellItem.X`, `.Y`
   - the base `Tick`
   
   A codec that only handles constructor parameters would drop these silently.
6. No commands hold delegates, `ItemInstance`, `GameRecord` or live entity references. Everything except `FcsRecord` is a value or an ID.

### Commands that need redesign
- **Selection fallback (Sel rows, about 30 commands)** and **P/1 rows:**
  - There is one `World.Player` (World.cs:126) holding Selection, Money, Squad, Inspected, OpenContainer and DoorTarget.
  - Ownership is a single `CharacterCold.IsPlayer` bool (CharacterTable.cs:74).
  - Single slots exist for trade (TradeSystem.cs:98-99), conversation (DialogueSystem.cs:81-83) and research (`world.Research`).
  
  For multiplayer:
  - Add an issuer id to `SimCommand`.
  - Make `PlayerState` per player.
  - Replace `IsPlayer` with "owned by the issuer's faction".
  - Make trade, dialogue and container sessions per player.
  - Have the client always send explicit character lists, or keep Selection per player inside the sim, since it is hashed (PlayerState.cs:104-107).
- **Ownership holes:**
  - `MoveOrder` and `AttackOrder` do not check owners.
  - The `Debug*` cheats, `StartContract` and `SandboxMovement` need host gating.
- **Trust flags a client could abuse:**
  - `AttackOrder.Defensive`: no assault crime or relation event.
  - `EditCharacter.Paid`: free surgery (CharacterEditing.cs:75-85).
  
  Strip them from network input and keep them for deferred commands or server-only use.
- **FocusCommand:** one global focus (PopulationSystem.cs:96,102) means only one camera's zones get populated. It needs a set of foci, one per player, and under (L) it becomes a synced gameplay command.
- **RepathCommand:** generated from wall-clock navmesh loading (GameHost.cs:311-325, using Stopwatch). Under (L) it diverges, because each peer loads at different times.
- **Paused path:** `ApplyPausedInterfaceCommands` (World.Interface.cs:9-18) applies about 15 command types between ticks, through only 4 systems, without advancing Tick. Pause and speed are host-side (`Simulation.IsPaused`, Engine/WorldSession.cs:88) and are not commands. Under (L), pause and speed must become synced control messages, and paused-applied commands need a deterministic slot such as (tick, sub-step).
- **EditCharacter:** sends a whole `FcsRecord`. It would be better as a compact appearance-delta DTO that the server validates.
- **UI-only commands** (`InspectCommand`, `InspectBuildingCommand`, `SeenDiscoveriesCommand`, `CloseContainer`): keep them client-local under either model. Under (S) they never need to reach the host, except where they change hashed state.