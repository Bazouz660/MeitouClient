## Snapshot and render/UI read surface (for choosing between lockstep (L) and state replication (S))

### 1. Boundary as built

- **The renderer is already isolated.** `Meitou.Rendering.csproj` references only `Meitou.Core` and `Meitou.Data`. It never sees a Simulation type. `GameHost` turns snapshots into `CharacterInstance` (GameHost.Characters.cs:88-131). docs/simulation.md:963 says "snapshots are the simulation-to-host boundary".
- **Publish point.** `World.RunTick` step 8 runs `if (Settings.PublishSnapshots) Publish();` (World.cs:232). Its output is `World.Snapshot` / `PreviousSnapshot` (World.cs:164-165). The host interpolates between those two.
- **Snapshot shape.**
  - `CharacterSnapshot` (WorldSnapshot.cs:51-143) has 5 positional fields and 43 `init` properties.
  - `WorldSnapshot` (WorldSnapshot.cs:161-217) has 2 positional fields and 23 `init` properties.
- **It is single-viewer.** `public PlayerState Player { get; } = new();` (World.cs:126). Publish reads `Player.Selection`, `Player.Inspected`, `Player.InspectedBuilding`, `Player.OpenContainer`, `Player.Towns/Camps/SquadAi/Squad`, `cold.IsPlayer`, the one `World.Research`, and `Machines` ("the player's complete buildings"). It also reads single per-system views: `TradeSystem.view` (TradeSystem.cs:171-174) and `DialogueSystem.view/request` (DialogueSystem.cs:352-353).

### 2. Every world read the host and UI make

**A. Through the snapshot.** There are 118 `CurrentSnapshot` reads in 39 files and 3 `PreviousSnapshot` reads.
- Top `WorldSnapshot` members: `Characters` 56, `Buildings` 17, `Trade` 14, `Conversation` 8, `PlayerSquads` 6, `Machines` 6, `Towns` 5, `BuildingsRevision` 5, `Doors` 4, `Research` 3, `GroundItems` 3.
- Top `CharacterSnapshot` fields read in Meitou.Game: `Body` 31, `Appearance` 12, `Task` 10, `Prison` 10, `ItemGrid` 9, `Animations` 8, `Jobs`/`Hud`/`PortraitSerial` 5 each.
- `Skills` and `Inventory` (the text lines) have **0 readers** in Meitou.Game, so they are dead weight on the wire (Observed by grep).

**B. Directly on `World`, `PlayerState`, systems or data.** Leaving out `Tick` and `Commands.Enqueue`, there are about 30 sites. These would need a replacement under S:

| Read | Site | Notes |
|---|---|---|
| `World.Tick` | 126 reads | Mostly stamping commands: `Say(new MoveOrder(..){ Tick = session.World.Tick })` (PlayerInterface.cs:65-105). |
| `Commands.Enqueue` | 29 direct + 91 `Say(new …)` | `Say` → `session.World.Commands.Enqueue` (PlayerInterface.cs:56-60). |
| `World.Player.Faction/Money/Exists/CreationRaces` | about 12 | Ui.cs:106, Editor.cs:276/288, Doors.cs:38, Frontend.cs:86-148, GameHost.cs:215/221. |
| `popData.Relations.IsHostile/Get/IsAlly` | GameHost.cs:215-224 | Live relations matrix, used for hostility and map standings. |
| `MapItems.Reveals(d, map, Towns, World.Seed)` | GameHost.cs:231 | Needs the seed plus town knowledge. |
| `World.CanTalk(npc, talker)` | PlayerInterface.cs:133 (3 sites) | Runs a dialogue **dry run** inside the sim: it rolls, then restores `rolls`/`permanent` (DialogueSystem.cs:242-256). |
| `World.Prospected` (polled by `Serial`) | PlayerInterface.Prospecting.cs:51-55 | |
| `World.TakeMessages()` | GameHost.Interactive.cs:662 | Fed by about 100 `World.Post` sites. |
| `CarrySystem.TakeKnockouts()`, then `Characters.Cold(k.Attacker.Slot)` for name and `IsPlayer` | Interactive.cs:668-671 | |
| `aggro.TakeLog`, `population.TakeTownLog/TakeWildLog` | Interactive.cs:672-683 | Console only, can be dropped. |
| `Characters.Previous[leader].Position` | GameHost.cs:253, NewGame.cs:63, Saving.cs:253 | Camera placement. |
| Queue hooks `world.Sounds = …`, `world.BloodMarks = …`, `world.Floaters = …` | AudioCharacters.cs:70, Blood.cs:55/59/102, Floaters.cs:29/45 | |
| Host config written into the sim | Blood.cs:52 `world.Luckyren = config.LuckyrenMode;`; Interactive.cs:113-121 sets `CombatSystem.AttackSlots/LateDefences`, `LockSystem.DoorsCloseBehind`, `PopulationSystem.{KeepRoamers,OffscreenFights,OffscreenHealing,…}` | These change the run (World.BloodMarks.cs:105-107: "Unlike the marks it changes the run"). |
| `session.Clock` / `World.GameHours`, `Simulation.Speed/IsPaused` | WorldSession.cs:84-96, GameHost.cs:346 | `GameHours` is an unhashed input (World.cs:183-187). |
| Saving | GameHost.Saving.cs | Reads the whole World. Host only. |

**C. Host navmesh upkeep.** The host diffs `Buildings` whenever `BuildingsRevision` changes (GameHost.Building.cs:28-41). It calls `nav.AddRuntimeBuilding` (:124-130). When a rebuilt zone publishes, it enqueues `RepathCommand { Tick = session.World.Tick }` (GameHost.cs:325, docs/simulation.md:644-649). Under S the client does not need this. Under L it is a frame-timed input (see section 6).

### 3. Bytes per character per tick (architecture S)

The fields that change every tick:

| Part | Raw in-memory form | Quantized estimate |
|---|---|---|
| `Id` (`CharacterId(int Slot, int Generation)`, CharacterId.cs:7) | 8 B | 2-4 B (index into a client table) |
| `Position` + `Yaw` (after `DrawnPosition`, WorldPublish.cs:15-20) | 16 B | 7-9 B (16-bit per axis relative to zone, 8-12-bit yaw) |
| `Animations`: up to `MaxLayers = 8` (AnimationSystem.cs:15). Each `AnimationLayer(string Name, float Time, float Weight){Length, Definition}` | about 16 B + a name reference per layer; typically 2-4 visible layers (Observed shape, count not measured) | 4-5 B per layer: definition index u16 (the name and `Length` come from the client's animation library), time u16, weight u8. That is 10-20 B. |
| Flags: `WeaponDrawn`, `InCombat`, `Task`, `Orders`, `RunSpeed`, `InBed`, `PickingLock`, `WorkingShackles`, `Shackled`, `CarriedBy`/`Carrying`, `Scale`, `Body` (`BodyStatus`: 3 floats + 7 bools) | about 40 B | Delta-coded: about 0-3 B per tick most ticks |

- **Ordinary character:** about 60-80 B raw per tick, or about 20-30 B quantized and delta-coded.
- **Ragdoll.** `BonePose` holds `bool[]`, `Quaternion[]` and `Vector3[]` sized to the **whole skeleton** (SkeletonRig.cs:50-56). Only the bones in `Set` (one per ragdoll body) matter (RagdollSystem.cs:537-556). Only the root uses `Position`.
  - Assuming about 15-20 bodies (the count is **Unknown**: neither the docs nor the tests state it), that is about 16 B rotation per bone + 12 B root: roughly 250-330 B raw. Smallest-three quantization brings it to about 100-150 B.
  - `r.Published = Build(...)` runs **every tick for every ragdoll, frozen or not** (RagdollSystem.cs:229). Frozen poses do not change, though, so a delta coder sends almost nothing.
  - Freezing happens after 2-7 s (docs/game/ragdolls.md:145). Ragdolls therefore cost bandwidth only in fights.
- **Appearance.** `CharacterAppearance` is an object graph (CharacterAppearance.cs:76). It cannot go on the wire as-is. It can be rebuilt client-side from `(RecordId, SavedKit)`, the same way `CharacterAssembly.RefreshEquipment` rebuilds it (CharacterAssembly.cs:97-111). That costs about 100-300 B per spawn or equipment change (estimate).
- **Rough total.** At 30 ticks/s (`TickSeconds = 1/30`) with N characters: about 0.6-0.9 KB/s per character. Publish emits **every living character** (WorldPublish.cs:36-38), not only those near the viewer, so 300 characters would be about 200-270 KB/s per client. Interest culling and a 10-15 Hz send rate (the host already interpolates, GameHost.Characters.cs:96-118) would bring that to tens of KB/s. A large fight adds about 20 ragdolls × 120 B × 30 Hz ≈ 70 KB/s. **Speculative:** the character counts are not measured.

### 4. Event streams (S must forward them; L gets them for free)

| Stream | Type / kinds | Sources | Notes |
|---|---|---|---|
| Sounds | `SoundEvent` (13 kinds, World.Sounds.cs:6-36) | 13 `EmitSound` + CombatSystem.Sounds.cs + AggroSystem.cs:394 | Display only, enqueued into a `ConcurrentQueue`. |
| Blood | `BloodMark` (5 kinds, World.BloodMarks.cs:74-92) | BodySystem.cs:69/204, CombatSystem.Blood.cs | `Explosion` carries a race id. The trail state on the cold row is unhashed (BodySystem.cs:98). |
| Floaters | `Floater` (Damage/Heal, carries `HitDamage` + `ArmourAbsorbed`) | CombatSystem.Apply.cs:189, `PublishHeal` | |
| Messages | `string` | about 100 `Post` sites | |
| Knockouts | `StealthKoEntry` | CarrySystem.StealthKo.cs:36 | |
| Prospecting | `ProspectReport` (by serial) | World.Prospecting.cs:10 | |
| Dialogue edge | `DialogueRequest` (by `Id`, acted on once) | snapshot | |
| Speech | `SpeechBubble` list, **state** republished every tick (DialogueSystem.Ambient.cs:23) | snapshot | |
| Animation-driven audio | none: derived on the client from `AnimationLayer.Definition/Length/Time` (AudioCharacters.cs:93-98) | | Needs the animation layer data at full precision. |

Each stream is about 30-60 B per event, and they come in bursts (fights).

### 5. UI-only deep reads (per viewer, sent on change)

These are built only for `selected || inspected` (WorldPublish.cs:42-67) or for the open window:

- **Stats:** `Hud` (`HudView` + 6 `HudStatLine` + `ExtendedStats`), `AllSkills` (the full stat array), `SkillXpMultipliers`, `HealthParts` (one `HealthPartView(Id, Name, 6 floats, bool)` per part), `Bounties`, `Path`.
- **Inventory grid:** `ItemGrid` (`InventoryView`, each item an `InventoryItemView` with 3 strings + 8 numbers + `Material` + `Charge`), plus the open container shaped as a `MachineSnapshot` (WorldPublish.cs:111-118).
- **Trade:** `TradeView` with stock and goods lists.
- **Dialogue:** `ConversationView` (text, choices, the full log).
- **Production:** `Machines` (slots, storage grids, farm with per-plant growth, power, craft queue), `Crafts` (per-bench option lists).
- **Research:** `ResearchSnapshot` (every available option with Name, Category, **Description** and Cost). Plausibly the largest view, at tens of KB raw (estimate).
- **Construction:** `Buildings` (each with a `MaterialView[]`), `UnlockedBuildings`, `InspectedBuilding`.
- **Map:** `Towns` (byte flags), `Camps`, plus relations and the seed outside the snapshot.
- **Squads:** `PlayerSquads`, `ActiveSquad`, `SquadAi`.
- **World objects:** `GroundItems`, `Doors`, `DoorPicks`, `Containers`.

Most strings in these views are names resolved from record ids. A client with the install could resolve them itself, but the current view records embed the resolved strings. S therefore needs either a parallel id-based wire schema or strings on the wire.

### 6. Questions on publishing

**Does Publish mutate hashed state? No.**
- The only state it writes is `CharacterCold.HealShown/HealTick` in `PublishHeal` (Floaters.cs:39-49). Those are documented "display only: not hashed, not saved" (CharacterTable.cs:140-142), and grep finds no other reference.
- Everything else Publish touches is a lazy cache: `BuildingRegistry.Published` (:245), `GroundItems.Published` (:68), `FixtureRegistry.PublishedDoors` (:731), `ContainerSpots`/`DoorPicks` (:49/:54), `Machines.Published` (:199), `ProductionSystem.ResearchView` (:704), `CraftsView` (:183).
- None of the `Hash` bodies of Buildings, GroundItems, Fixtures or Machines mention these caches (checked by grep).
- Tests turn publishing off (`PublishSnapshots = false`, 48 sites) without changing the hashes.

**Is it cheap to publish for another viewer? Not as written.**
- There is no viewer parameter. Viewer-dependent logic is woven into the per-character loop: `koSlot` comes from `Player.Selection` (:29-31), plus `selected`/`inspected`/`sneaking` (:42-44), `NeedsAid`/`NeedsSplint` keyed on `cold.IsPlayer` (:155-159), `SurgeryCost`, `CanBeShackled`, `StealthKoChance`.
- World-level per-player state exists only once: one `PlayerState`, one open trade, one conversation, one `Research`, one player `Machines` set, `IsPlayer` as a bool with no owner id.
- Each Publish is O(N) allocations: a new `CharacterSnapshot` and layer array per character (AnimationSystem.cs:678-711), `inventory.Items.Any(...)` per character, and `talk.Trades` / `SurgeryCost` lookups per NPC.
- A second viewer would therefore need two changes. First, a `PlayerState` per player, plus per-player trade, dialogue, research and container state. Second, splitting Publish into a shared per-character core (transform, animation, ragdoll, flags) and a per-viewer overlay. `PublishHeal` is not idempotent across viewers: the first publish consumes the heal amount.
- Publish also re-runs between ticks while paused (World.Interface.cs:186-191).

### 7. How big the replication surface is

- **Data kinds:** 48 `CharacterSnapshot` fields, 25 `WorldSnapshot` fields, about 30 nested view records, 7 event streams, and about 10 out-of-snapshot reads (Player money/faction, relations, seed, `CanTalk`, `Prospected`, messages, knockouts, clock, speed/pause, leader positions). Roughly **45-50 distinct data kinds**.
- **Call sites:** about 120 snapshot reads, about 30 direct World/system reads, 126 `World.Tick` reads and 120 command-issue sites.
- **Under S:** every item needs a wire schema, delta and interest logic, and client-side reconstruction (appearance kits, name resolution). `CanTalk` needs an RPC or a per-viewer flag, because it executes dialogue logic. Per-viewer publishing has to be built anyway. Bandwidth is about 0.5-1 KB/s per visible character, plus bursts.
- **Under L:** the whole read surface stays local and costs 0 bytes, since each peer runs `World`, `Publish` and the event queues itself. The work moves elsewhere:
  - **Per-viewer publish.** The same refactor as S, minus the wire format.
  - **Local config that changes the run.** `Luckyren` and the combat, lock and population settings become session settings shared by all peers.
  - **Command timing.** Commands need an input delay (they are stamped `Tick = World.Tick` today).
  - **Paused commands.** `ApplyPausedInterfaceCommands` changes state outside ticks (World.Interface.cs:165-192), so pause or paused commands must be global and ordered.
  - **Frame-timed host inputs.** `FocusCommand` (single focus, PopulationSystem.cs:102) and `RepathCommand` are issued per frame and must become deterministic inputs.
  - **Async services.** Paths, navmesh and population results enter at tick boundaries with timing that "is not repeatable in play" (docs/simulation.md:39-40). Lockstep needs these tick-quantized or synchronous.

On the read/render side alone, L is far cheaper. The surface is wide and UI-heavy, and the renderer seam (snapshots) is already clean for local use. Whether L works overall depends on making those inputs deterministic, which is outside this area.