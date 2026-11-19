# 04. Replication

What a client needs from the server and how it gets it. Upstream `a1ef2e06f`. `Sim/` = `src/Meitou.Simulation/`, `Game/` = `src/Meitou.Game/`.

## Today (facts)

- `World.Publish` builds a `WorldSnapshot` at the end of a tick (`Sim/WorldPublish.cs:146-205`; `Sim/World.cs:834`). It holds **every
  living character**, with no filter (:192-195), in a ring of four buffers whose `CharacterSnapshot` objects are reused; a published
  character stays valid until two more snapshots publish (docs/simulation.md:37-43).
- `WorldSnapshot` has `Tick`, `Characters` and 55 more properties (`Sim/WorldSnapshot.cs:939-1163`). About 20 are the same for every
  viewer (buildings, ruins, ground items, containers, beds, seats, cages, doors, machines, projectiles, speech), most with a revision
  counter (`BuildingsRevision`, `GroundItemsRevision`, `MachinesVersion`, ...). About 35 are built from the one `world.Player`: trade,
  conversation, towns and camps known, squads, held item, open loot, outposts, map squads, research, crafts, the inspected building.
- `CharacterSnapshot` has 102 properties, all written at every publish (`WorldSnapshot.cs:389-868`). Some depend on the viewer:
  `Selected`, `FirstSelected`, `Inspected`, `FightsPlayer`, `HostileToPlayer`, `InPlayerBuilding`, `CanStealthKo`, `StealthKoChance`,
  `KidnapChance`, `Activity`, `Stealth`, `Light`, `Watchers`, `Bar`, `CanOpenInventory` (`WorldPublish.cs:386-413,453-455,573-660`).
  Skills, inventory, health parts, path and the like are filled only for selected, inspected or pointed characters.
- The game draws between two snapshots with an alpha: position, yaw, look-at, carry attach, animation layers and ragdoll bones are
  interpolated (`Game/GameHost.Characters.cs:140-171,444-486,801-828`).
- The simulation chooses animation layers (clip name, time, weight) (`Sim/Movement/AnimationSystem*.cs:2131`); the game samples the clips
  and computes bones. Ragdolls are simulated in the world, hashed and saved, and published as bone poses (`WorldPublish.cs:647`;
  docs/simulation.md:904-934).
- Nothing serialises a snapshot. No shared/personal split exists.

### Events (facts) {#events}

Things that happen once reach the game through queues the host installs on the `World` and drains each frame. They are not in the
snapshot, have one consumer and no recipient:

| Event | Where | Drained at |
|---|---|---|
| Sounds (`SoundEvent`) | `World.Sounds` (`Sim/World.Sounds.cs:131-160`) | `Game/Audio/CharacterAudio.cs:198` |
| Floating damage text (`Floater`, with flags for "the active squad") | `World.Floaters` (`Sim/Floaters.cs:61,132`) | `Game/Ui/DamageFloaters.cs:66-73` |
| Blood (`BloodMark`), block sparks | `Sim/World.BloodMarks.cs:45,63`; `World.Sounds.cs:166` | `Game/Effects/BloodDecals.cs:134,320` |
| Messages (`World.Post(string)`, 169 call sites in 42 Simulation files) | `World.cs:1057,1074` | `Game/GameHost.FrameShared.cs:155` |
| Said lines, event log, dialogue log, knock-outs, town and wild logs | `World.cs:1105`; `FrameShared.cs:166-186` | same |
| Prospecting reports, arrival banter | `Sim/World.Prospecting.cs:19`; `Sim/World.Verbs.cs:54` | `Game/Ui/ProspectingPanel.cs:89-95`; `Game/Ui/ArrivalBanter.cs:49` |
| Speech bubbles | in the snapshot (`WorldSnapshot.Speech`) | - |

Footsteps and animation sounds are made on the game side from the animation layers.

### Live reads (facts) {#live-reads}

The inventory tool counted 2,670 places where the game reads simulation state that is not a snapshot type (`../research/state-inventory.md`,
at `315d26ecf`; field and property reads only, method calls are not counted). Sorted by what they are:

| Group | Places | What it means for a remote client |
|---|---|---|
| Value objects that already arrive inside snapshots (`TradeItemView`, `EfficiencyView`, `ResearchOption`, `ContainerSpot`, `HeldItemView`, `FarmView`, `CageSpot`, `DoorPick`, `CharacterId`, ... 54 types) | about 1,128 | Nothing to do: they are data once the snapshot is rebuilt. This is most of ProductionWindows, BuildingSelection, FurnitureUse, InventoryWindows, TradeWindows, TooltipRuntime |
| Immutable data (`PopulationData`, `BuildingFootprint`, `PlacedBuilding`, `TownSite`, `WeaponStats`, navmesh types) | about 300 | The client loads the same data; the join check guarantees it ([06](06-transport-and-join.md)) |
| `World.Tick` | 322 (324 now, 268 on a command-stamp line) | `IClientSession.Send` stamps; the rest read `ShownTick` |
| Event records (`BloodMark`, `Floater`, `SoundEvent`, prospecting) | about 130 | Arrive on the event stream |
| True live state (`World.Player` 94, `PlayerState` 80, `Fixture` 62, `CombatSlot` 26, `Squad` 23, `CharacterCold` 21, `CharacterHot` 20, `GroundItem` 20, clocks 25, profile and systems 42, ...) | about 480 | See below |

Of the last group, the developer tools account for most (AgentWorld 144, AgentOrders 143, DebugMenu 92, ScreenshotScenes 55, AgentPlay 54,
BattleScenario 46, SandboxScenario 35, AgentCombatWatch 24: 593 reads across all groups, old commit). What is left in the shipping UI and
host, checked at `a1ef2e06f`:

| Place | Reads | Replacement |
|---|---|---|
| `Game/Ui/ContextMenuWindow.cs:164,435,557,624` | `World.CanTalk`, `World.VerbFactsOf` | Verb facts for the pointed character join the personal view (it already carries the pointed character's context figures, docs/simulation.md:97-99) |
| `ContextMenuWindow.cs:304,643,681`; `Game/Ui/FurnitureUse.cs:200,376,419,448,460`; `Game/Building/BuildingSelection.cs:102,603`; `Game/Building/BuildMode.cs:1211`; `Game/Ui/TownPanel.cs:74,100` | `Player.Faction` | `PersonalView.Faction`; ownership tests become "owner is my faction" on snapshot data |
| `Game/Host/WorldSync/BuildingSync.cs:128,272,538,482,590` | `Player.Faction`, `Fixtures.Saved`, `Fixtures.SavedDestroyed` | Faction from the personal view; saved and destroyed flags join the shared building view |
| `Game/Ui/OrdersPanel.cs:196` | `World.Systems` | A list of order rows in the server info |
| `Game/Tutorials/TutorialPanel.cs:141,312,574` | `Player.Exists` | Personal view |
| `Game/GameHost.FrameShared.cs:155-186`, DamageFloaters, BloodDecals, CharacterAudio | queue drains | `IClientSession.Events` |
| `Game/Effects/Decapitation.cs:165-446`, Debris | `World.TickSeconds`, `Walkability.GroundHeight` | Constants and the client's own terrain |
| `Game/Host/WorldSync/WeatherFeed.cs:37` | `World.RegionAt` | Client-side weather presentation ([01](01-server-host.md#weather-without-a-camera)) |
| `Game/Ui/PointerView.cs:145` | writes `Player.Pointed` | A `Point(id)` message, at most a few a second |

So the "2,670 reads" problem is about 60 places in shipping code plus a seam for the developer tools. **Not checked:** WorldMap and
WorldPicking line by line; files outside the old top-30 table.

## Target (proposal)

### Two views

- **Shared view**: characters without their viewer fields, and the shared lists. Built once per publish, as today.
- **Personal view**, one per player: everything built from `world.Player` today, plus a small **viewer overlay** per character in that
  player's interest (the 15 viewer fields above) and the details of the characters that player selected, inspects or points at.

`WorldSnapshot` and `CharacterSnapshot` keep their shape, so the 3,167 snapshot reads in the game do not change. For the local player the
host fills the viewer fields in place, as now. For a remote player the server sends the personal view and the overlay, and the client
writes them into its own `CharacterSnapshot` objects. `Publish` gains a loop over players for the personal part; its cost per extra
player is the detail work for a handful of characters (today's reason to limit detail to the selected, docs/simulation.md:97-102).

### Interest {#interest}

The unit is the zone (4608 units square, `Data/World/WorldLayout.cs:13-18`). A player's interest set is:
- the 3 x 3 block round their view anchor, and
- the zones touched by the load box round each of their characters (the same zones that player keeps loaded, [05](05-areas.md)).

At each publish the server buckets character slots by zone once (O(characters)), so a client's candidate list is the concatenation of at
most a few dozen zone buckets, not a test of every character against every client. A zone enters a client's set at once and leaves after
a few seconds' linger, to avoid flicker at borders. Buildings, doors, ground items and machines are per zone too and already carry
revision counters: a zone's list is sent when the client enters and when its revision moves.

What a client is never sent: characters outside its interest; other factions' inventories unless open to it (looting, trading, an
inspected downed body); other players' paths, goals (`Activity`) and stealth figures; any personal view but its own.
The original shows every loaded character regardless of sight, and so does this. Hiding unseen sneaking players from enemies is a later
PvP rule, not in the first milestone.

### What is sent, and how often

| Stream | Content | Delivery | Rate |
|---|---|---|---|
| State | Per character in interest, only fields that changed since the last update the client acknowledged: position, yaw, animation layers, task, flags, carried-by, body status, ragdoll | unreliable, sequenced; each update names its baseline | by priority, below |
| Spawn / despawn | Identity and appearance of a character entering interest (name, race, look, gear, scale, faction, squad); removal | reliable, ordered | on change |
| World lists | A zone's buildings, doors, ground items, containers, beds, cages, machines | reliable; whole list per zone on revision change, later per item | on change |
| Personal | The personal view, cut into parts with a version each (squads, selection details, inventory grids, trade, conversation, research, map, held item) | reliable, ordered; only changed parts | on change, at most 10 a second |
| Events | Sounds, floaters, blood, sparks for zones in interest; messages addressed to the player | unreliable for effects, reliable for messages | as they happen |
| Globals | Clock, speed, vote state, weather per region, player list, relations between player factions | reliable | on change; clock every second |

**Priority and budget.** Each client has a byte budget (rule `net.clientBudget`, default 32 KB/s = 256 kbit/s). Each character in
interest has an accumulator that grows every tick by a weight: highest for the client's own characters and whoever fights them, high
near the anchor, lower with distance, zero for a character with nothing changed. Each send (every 2 ticks, 15 a second) takes the
highest accumulators until the packet budget is used and resets those sent. So a quiet scene sends little, a crowded one degrades far
characters first, and the budget is never exceeded whatever the player count (the scheme of `../research/research-web.md`, section 2).
Idle characters cost nothing: only changed fields are written.

**Encoding** (sizes are arithmetic, not measured):

| Field | Wire size | Note |
|---|---|---|
| Character id | 2-3 B | The server's `CharacterId` (slot and generation) is used as is: the client never makes ids, so no mapping is needed. Commands already name characters by it |
| Position | 6 B | 16 bits per axis relative to the zone (7 cm steps) |
| Yaw | 1 B | |
| Animation layer change | 4-5 B per layer | Clip by index in a string table; time and weight quantised. Between changes the client advances clip time itself |
| Flags, task | 1-2 B | |
| **A walking character, per update** | **about 10-14 B** | The October test branch observed 1.2 KB per send for 83 characters, about 14.5 B each (`docs/net.md` on `multiplayer`, synthetic scene) |
| Spawn (identity + appearance) | 300-600 B | Assumed; `CharacterAppearance` has 28 members and was not measured |
| Ragdoll pose | about 100-130 B per update | Root position plus 4 B per bone ("smallest three" quaternion), assuming 25-30 bones; bone count **not checked**. Sent at 10 a second while the body moves; a body at rest sends once |
| Naive full character, for comparison | 350-450 B | Field types added up from `WorldSnapshot.cs` |

### Bandwidth {#bandwidth}

Assumptions: a quarter of a scene's characters within 2000 of the anchor get 15 updates a second, the rest 7.5; 40 % of characters are
changing at any moment; 14 B per update; 2 KB/s for headers, events and the personal view. None of this is measured.

| Scene | Characters in interest | Per client, down |
|---|---|---|
| Wilderness | 30 | about 20-30 kbit/s |
| Busy town | 400 | about 170-200 kbit/s |
| Large battle (200 fighting, all changing, 25 B each) | 400 | hits the 256 kbit/s budget; far fighters drop to about 6 updates a second |
| Client, up | - | about 10 kbit/s (commands, view reports, acknowledgements) |

| Players | Remote clients | Server upload |
|---|---|---|
| 2 (host + 1 friend) | 1 | 0.03-0.26 Mbit/s |
| 8 | 7-8 | 0.8-2 Mbit/s |
| 32 | 32 | 3-8 Mbit/s |
| 200 | 200 | 20-51 Mbit/s |

A home connection hosts a few friends comfortably. The budget is a rule; halving it halves the right-hand column and lowers far update
rates.

Server CPU for replication is **assumed** at 20-50 µs per client per send (walk a few hundred characters, compare, write): 15 sends a
second for 200 clients is 6-15 % of one core, off the tick thread. To be measured in package 16.

### Joining late

A joiner gets: the globals, its personal view, then the spawn records and zone lists of its interest set, on the reliable streams with
a separate, larger budget (rule, default 1 Mbit/s). For a busy town that is assumed at 0.3-1 MB before compression, so 3-8 seconds,
during which the client shows its loading screen and loads terrain and assets for the place (it knows where its characters are from the
first message). The world does not wait. This is not a save transfer: a save does not restore orders, paths, animation or combat state
and its hash differs after loading (docs/simulation.md:2203-2206), so it could not serve as a join snapshot anyway.

### What the client does with it

`RemoteSession` is a playback machine that makes `WorldSnapshot`s the game cannot tell from local ones:
- It keeps, per character, the last few received states with their server ticks.
- It runs its own 30 Hz "display tick". At each it produces a snapshot for the time `now - delay`, sampling each character between the
  two states that bracket that time, and advancing animation clip time itself. The delay is two update intervals plus measured jitter
  (about 130-200 ms). The game's existing interpolation then runs between these snapshots exactly as between simulation ticks.
- A character with no bracket (loss, late packet) holds its last state; after 0.5 s without news it is shown standing.
- It keeps the ring contract: objects handed out stay untouched until two more snapshots.
- Ragdolls are shown from the received poses with the existing bone blend.
- Local-only effects stay local: birds, debris, decapitated heads, blood decal drawing, footsteps, weather presentation.

No client-side prediction of the world ([03](03-commands-and-authority.md#latency)).

### Events and messages

- Effects (sounds, floaters, blood, sparks) carry a position; the server copies them at the tick's end into the event stream of every
  client whose interest holds that zone. `Floater`'s "is the active squad's" flags are worked out on the client from the owner.
- `World.Post(text)` gains an audience: `Post(audience, text)` with a player, a faction, or "everyone within earshot of a point".
  The 169 call sites each need the right audience; `Post(text)` stays as "player 0" until its site is visited, which is correct for
  single-player throughout. This is package 12 of [08](08-milestones.md).
- The host's log taps (event log, action log, dialogue log) stay on the server.

## What must not change in single-player

- The local session hands out the host's snapshot objects by reference: no encoding, no playback delay, same ring.
- `Publish` for one player does the same work in the same order; the viewer fields are filled in place.
- The event queues keep their types; the local session drains them as `GameHost` does now.

## Not checked

- Bone count of the rigs; size of `CharacterAppearance` on the wire; how often animation layers change in a real town.
- Whether any shared list lacks a revision counter (`Containers`, `Beds`, `Seats`, `Dummies`, `DoorPicks` show none in the property list).
- Per-publish cost of a personal view for a second player.
