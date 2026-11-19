# 07. Settings, mods, saves, offline players

What belongs to the server. Upstream `a1ef2e06f`. `Sim/` = `src/Meitou.Simulation/`, `Game/` = `src/Meitou.Game/`,
`UC` = `Game/Host/UserConfig.cs`, `GS` = `Game/Host/GameSaves.cs`.

## Settings

### Today (facts)

Gameplay values sit in three places, all on the player's machine, and are pushed into the simulation by the game; nothing in Simulation
reads the config:

| Group | Values | Declared | Pushed |
|---|---|---|---|
| Population and off-screen switches | `KeepRoamers`, `OffscreenFights`, `OffscreenHealing`, `CampaignAncestorsOnce`, `KeepWild`, `ResidentSchedule` | UC:362-385 | build GS:691-696; live `Game/Host/GameLoop.cs:538-546` |
| Combat and AI | `AttackSlots`, `LateDefences`, `FixGunnerFacing`, `FixPunchReach`, `FixAnimalMoves`, `AnimalsPickUp`, `CagesSpareOwn` | UC:346-357,438-473 | build GS:705-716; live `GameLoop.cs:511-522` |
| Bodies (a shared mutable `PopulationData.BodyOptions`) | `Decapitation`, `AdvancedRagdolls`, `SquadXp`, `OthersXp`, `Dismemberment` | UC:395-409 | `Game/GameHost.cs:723-727`; live GS:656-658 |
| World flags | `FixBenchFacing`, `FixPrisonPose`, `FixCagedAnimals`, `FixBagSearch`, `TakeIntoBackpack` | UC:478-492,541 | `GameHost.cs:650-654`; live `GameLoop.cs:516-520` |
| Production | `UnloadedProduction` | UC:390 | build GS:708; live `GameLoop.cs:549` |
| Time | `FastSpeed`, `FastestSpeed` | UC:658-662 | `GameHost.cs:656` |
| Items | `GroundItemLimit` | UC:667 | every frame, `Game/Host/WorldSync/GroundItemSync.cs:43` |
| Terrain | `FixGround` | UC:517 | static at startup, restart only (`Game/Host/Program.cs:83`) |
| The original's gameplay page | `SquadSize`, `Population`, `RaidSize`, `RaidFrequency`, `FastZoneHopping`, `TownAttacks`, `HarpoonLimit`, `Dismemberment` | `Game/Settings/GameplaySettings.cs:13-22,54-63` (first read from the install's `settings.cfg`, then kept in `meitou.user.json`, GS:645) | build GS:698-713; live GS:663-669 |
| Save policy | `AutoSave`, `AutoSaveMinutes` | same file | GS:109-112 |
| New-game difficulty (already per save, in CAMERA) | hunger, death, damage, production, research, building, nests; `bl` bandits loot the player | `Game/Title/NewGameSettings.cs:23-42` | at world build only (GS:449-453,697-708) |
| Engine | `FarTickPeriod` (a constant 4, `GameHost.cs:580`), `SimThreads` UC:684, `TickAhead` UC:341, `PauseOnLoad` UC:422, `LoadingPause` UC:427 | | |

That is 43 gameplay values plus 2 for autosave. Most can change while a world runs; the new-game seven and `FixGround` cannot. About 80
of `UserConfig`'s 120 properties are the client's own: graphics, audio, interface, camera, controls, bindings, censorship, the audio and
visual fixes. Code mods' settings pages keep their values in the player's `meitou.user.json` under `modSettings` (docs/modding.md:659-675).

### Target (proposal)

**`WorldRules`**, a record in Simulation, holds the gameplay values with the same names. `RulesBinder` in the host
([01](01-server-host.md#worldhost)) is the only code that turns rules into `StandardSystemOptions` and applies live changes, replacing
`BuildSystems`, `ApplyGameplay`, the callbacks of both frame loops and the tool's hand copy.

- **Single-player:** the rules are read from and written to the player's config exactly as now. The settings pages edit them; nothing
  the player sees changes.
- **Hosting for friends:** the host's own rules apply. Friends' settings pages show the Gameplay, Fixes and Advanced values greyed, with
  the host's values and "set by the host".
- **Dedicated:** `server.json` beside the save. A missing value takes the default, so a config stays valid across versions.
- A rule changed while running reaches every client in the globals stream; a client applies the few that affect its picture
  (`Dismemberment`, `FastSpeed` labels, `GroundItemLimit`).
- `FixGround` is part of the data identity ([06](06-transport-and-join.md#version-and-data-checks)); a client with the other value
  restarts with the server's, like a mod change.
- `TakeIntoBackpack` is a control preference that changes where an item lands. It becomes a field of the commands it affects, so each
  player keeps their own.

`server.json`, by section (all proposals; every value is a rule an admin can change, most while running):

| Section | Values |
|---|---|
| `server` | name, description, port, transports, max players, password, whitelist mode, admins, bans, `allowOtherVersions` |
| `rules` | the 43 gameplay values above |
| `time` | `mode` (local, vote, host, fixed), `voteSeconds`, `pauseAllowed`, `maxPauseSeconds`, `pauseWhenEmpty` ([03](03-commands-and-authority.md#time-speed-pause-and-votes)) |
| `players` | `maxSquads`, `maxSquadSize`, `maxCharacters`, starts allowed, `allowImport` (bring a single-player squad), `allowInPlayEditor`, `newFactionPerPlayer` |
| `pvp` | `mode` (off, consensual, on), `friendlyFire`, alliance notice period |
| `trade` | `players`, `characters`, `money`, `remoteMoney`, `betweenFactions` ([03](03-commands-and-authority.md#trade-between-players)) |
| `offline` | `logoutDelay`, `dangerDelayMax`, `captives`, `baseProtection`, `baseProduction`, `healing` (below) |
| `view` | `leash`, `maxGroups`, preset ([05](05-areas.md#what-is-loaded)) |
| `simulation` | threads, far period, near radius, navmesh zone limit, load-shedding thresholds |
| `net` | client budget, join budget, send rate, command rate, timeouts |
| `saves` | autosave minutes, slots kept, save on empty, save on shutdown |
| `mods` | the list in order; per code mod: side and settings |
| `cheats` | whether debug commands are accepted from admins |

Presets fill it: **Friends** (vote time, PvP consensual, no leash, protection on), **Community**, **Large** (fixed time), **Faithful**
(every Faithful switch as the original).

### Administration {#administration}

- The dedicated server's standard input and the existing agent channel (`--control-dir`, `Game/DevTools/Agents/AgentChannel.cs:18`) take
  console commands; an admin's game takes the same commands in its console, sent as admin commands through the gate.
- New commands: `players`, `kick`, `ban`, `unban`, `whitelist add|remove`, `admin add|remove`, `rule <name> <value>`, `save [name]`,
  `say`, `player link`, `player reset`, `faction` (list, rename, set relation), `tp` (cheats on), `shutdown [minutes]`, `status`
  (tick time, loaded characters, zones, per-client bandwidth).
- No remote console protocol in milestone 1; an operator uses a terminal or a file.

## Mods

### Today (facts)

- Data mods: the only order is `mods.cfg` (DECISIONS.md:23); `GameDatabase.Load` reads and merges them once per process
  (`src/Meitou.Data/GameDatabase.cs:91`).
- Code mods: one `AssemblyLoadContext` each, loaded at startup, no unload (docs/modding.md:104); the contract namespace `Meitou.Modding`
  is in the game executable's assembly (`Game/Mods/`); mods reference `MeitouClient.dll` (docs/modding.md:26-32). The manifest
  (`src/Meitou.Core/Content/CodeModManifest.cs:12`) has `id`, `apiVersion` (1-2), assemblies, entry, and with API 2 `version`,
  `dependencies`, `loadAfter`, `incompatible`, `gameVersion`.
- What a mod registers, by where it lands (`Game/Mods/ModContext.cs`):

| Side | Registrations |
|---|---|
| Simulation (no window needed) | `AddSystem` :200, `AddDialogueCondition`/`Effect` :214/:225, `AddRecordType` :237, `AddWearRule` :267, `AddStatusEffect` :314, `AddCombatMove` :334, `AddItemUse` :347, `AddJobType`/`AddGoalType` :362/:378, `AddWorldState` :449; the observer and filter interfaces a system implements; the data phase `EditData` |
| Both (state in Simulation, shown by the UI) | `AddEquipmentSlot` :251, `AddMedicalMeter` :279, `AddSkill` :296 (calls `Game.Ui.StatsWindow.ModRoom`, :301) |
| Game only | `AddUi` :397, `AddCombatHitFeed` :410, `ArrangeFactionList` :423, `AddDebugCommand` :434, `AddKeyAction` :468, `AddSettings` :489 |

- Mod save data: `IModSaveData` per system in `meitou-mods/<id>.dat`; per-character data by lasting id (docs/formats/save.md:183-217).
- Mod rules: no world access outside the tick, input through `Enqueue`, randomness through `Rng.Hash` (docs/modding.md:969-974).
  `mod-check` runs a mod headless three times and compares hashes (:976-993).

### Target (proposal)

**Data mods**: the server's list and order are the world's. A client must hold the same data
([06](06-transport-and-join.md#version-and-data-checks)).

**Code mods** {#code-mods}. Because clients do not simulate, a mod's simulation side runs **only on the server**. The manifest gains
`side` (API version 3):

| `side` | Runs on | A client needs it? | Examples |
|---|---|---|---|
| `server` | the host | no, unless it has a data phase: then the client runs only its `EditData`, so the merged data matches (the fingerprint in `CodeEdits` proves it) | a new AI goal, a combat rule, a spawn system |
| `both` (the default for mods without the field) | host and clients | yes, same version and assembly hash | a mod with a skill, a meter or a slot plus its window |
| `client` | clients | optional; the server's rule `mods.clientMods` is `any`, `listed` or `none` | a UI tweak, a hotkey, a camera mod |

- On a client, systems a mod adds are **not** put into a world (there is none); its registrations that describe things the UI shows
  (slots, meters, skills, status effects, record types) are kept, and its UI registrations run as today.
- On a dedicated server (the game executable without a window, [01](01-server-host.md#three-ways-to-run)) the Game-only registrations
  are accepted and ignored, so an existing mod loads unchanged. `AddSkill` must stop refusing for lack of UI room there.
- **A mod's own commands** (`SimCommand` records defined by the mod) cannot cross the wire unless the mod registers a codec
  (`AddCommandCodec<T>`, new). Without one, the command works only when enqueued on the server.
- **A mod's own view data**: today a mod's UI reads what the simulation publishes. For data the snapshot does not carry, a mod system
  may publish a small blob per player (`AddViewChannel`, new), delivered to the same mod on that player's client.
- **Mod settings** of a server-side mod move from the player's config to `server.json` `mods`.
- A `client` mod cannot change the world: it has no world. It can read everything the client was sent, which is why interest filtering,
  not the client, is what keeps secrets.
- Harmony patches (docs/modding.md:102-122) patch whatever process they are in. A `server` mod patching Simulation behaves the same on
  a dedicated server as in single-player.
- `mod-check` stays the determinism test. It does not show multiplayer faults; a `--players N` mode that runs scripted players
  through the gate is a later tool.

The move of `Meitou.Modding` out of the Game assembly (planned in CLAUDE.md) is needed only for the slim server executable. Old mods
keep binding if `MeitouClient.dll` forwards the moved types (`TypeForwardedTo`); **not checked** that forwarding from the executable's
assembly works for every mod as built today.

## Saves

### Today (facts)

- A save is the original's folder: `quick.save` (CAMERA, clock, factions, relations, research, platoon index), one
  `platoon/<faction>_<n>.platoon` per squad, `zone/zone.X.Y.zone` per visited zone, and Meitou's extras (`meitou-mods/`,
  `environment.json`, `character-ids.json`, `meitou-weapon-blood.json`, CAMERA `meitou seed`) (docs/formats/save.md:170-201).
- Tied to the one player: CAMERA `player money`, `pfaction name`, squad AI bools, formation, `bl`, the camera view and the selection
  (`Sim/Saving/SaveCapture.cs:161-176,2042-2063`); the player faction is the first GAMESTATE_FACTION with a `known` list, else id
  `204-gamedata.base` (`src/Meitou.Data/Save/SaveGame.cs:272-273`); research is one record (`SaveCapture.cs:214`); outposts are written
  for one faction id (:215-216); a platoon is "mine" when its faction is the player's (`Sim/Saving/SaveLoader.cs:333`).
- Capture runs serially at a tick boundary on the ticking thread (GS:219); the write is a background task (GS:238-254). The base copy
  of a large save is "seconds of a worker" (docs/engine.md:54). **No capture or write timing is documented.** Every NPC platoon ever
  loaded is written each time, with no dirty filter (docs/formats/save.md:572).
- Not restored: orders and paths, animation, combat engagement, ground items; a loaded world's hash differs from the captured one
  (docs/simulation.md:2203-2206).
- Autosave: real unpaused minutes, three rotating slots (GS:99-116).

### Target (proposal)

- **Where:** on the server only. A client never writes a world save. Dedicated: `--save-dir`, default beside `server.json`.
- **Format:** the same folder, with additions that an older reader ignores:

| Addition | Holds |
|---|---|
| `meitou-server/players.json` | each player: id, identities, name, faction, purse, squad AI, active squad, selection, last seen, role, offline state |
| `meitou-server/factions.json` | each runtime player faction: its index and id (`meitou-pf-<n>`), name, members, diplomacy with other player factions, map knowledge, rank |
| a GAMESTATE_FACTION per runtime faction | relations to NPC factions, with the original's keys (`relationSID<k>`, ...; `src/Meitou.Data/Save/SaveEntities.cs:57-132`) |
| per platoon | `meitou owner <player id>`, beside the existing `meitou autonomy owner`, `meitou purse` keys (docs/formats/save.md:421-431) |
| research per faction | the original's record for the primary faction; `meitou-server/research-<faction>.json` for the others |
| `meitou-server/rules.json` | the rules the world was last run with, for information |

- **The primary player.** Player 0 and the original's player faction keep every original key (CAMERA money, `pfaction name`, ...). On an
  internal server player 0 is the host. On a dedicated server player 0 is a reserved, empty "server" player, so no person's state lives
  in the special keys.
- **Opening a server save in single-player Meitou** works: the world continues with player 0; the other players' squads are there as
  stored, offline squads. **Opening it in the original game is not supported**: the runtime factions and owner keys mean nothing to it,
  and how it treats platoons of an unknown faction is unknown.
- **Starting a server from a single-player save** works: the save's player becomes player 0.
- **A player's part.** Everything of a player is in the server's save. `allowImport` lets a joiner bring the squads of one of their
  single-player saves (the existing `SaveImport`, `Sim/Saving/SaveImport.cs:61,190`, keeps only player squads); the gate checks the
  server's limits. Exporting your squads from a server to a single-player save is the same code the other way; off by default.
- **Autosave** runs on the server's clock (rule; default 10 minutes), plus on shutdown and when the last player leaves.
  **Risk:** the capture is on the tick thread and its cost on a large world is unknown; it will be a visible hitch for everyone if it is
  long. Package 18 measures it first. Remedies in order: write only platoons and zones changed since the last save; capture in slices
  over several tick boundaries.
- A save made while a trade is open drops its buy-back receipts, as today (docs/simulation.md:1862).

## Offline players and bases {#offline-players-and-bases}

The owner asked for an opinion. This is it, with each choice a rule so a server can differ.

### Fact that shapes it

There is no stored form of a player's squad today. `StoredSquad` is made only for squads the population created
(`Sim/Population/PopulationStored.cs:9-22`); player characters always stay in the table and keep their zones loaded
(`Sim/Population/PopulationSystem.cs:694`). The record itself fits: `StoredMember` is a character's whole cold state, place and yaw
(`Sim/Factions/Platoons.cs:251,313`). A stored NPC squad may travel, fight (`OffscreenFights`) and heal on return (`MedicalState.Recover`,
`Sim/Population/PopulationOffscreen.cs:240-264`); a player's must not.

### Characters

| Situation at disconnect | What happens | Why |
|---|---|---|
| Safe | The characters stay for `logoutDelay` (default 30 s), then each squad is stored where it stands and leaves the world | A crash or a dropped connection should not make characters vanish and reappear |
| In danger: in a fight, an enemy that senses them within range, being carried by someone not of their faction, bleeding out | They stay, with their standing orders and self-defence as an unattended squad has today, until 60 s after the danger ends, at most `dangerDelayMax` (default 10 minutes) | Leaving is not an escape. The cap exists because a server cannot keep a zone loaded for hours for nobody |
| The cap runs out mid-fight | Conscious characters are stored. The unconscious stay as bodies until they wake, die or are taken | Otherwise the cap is the escape |
| In a cage, shackled, enslaved, in a bed in a jail | They stay in custody. They are not stored: the captor holds them, as an NPC prisoner. Jail sentences run on the game clock, as they already do in saves (docs/simulation.md:2208-2210). `offline.captives`: `safe` (default: cannot be harmed, eaten, sold or moved further while their owner is offline, and are not freed either) or `as online` | A jailed character should serve its time; a kidnapped one should not be eaten while its owner sleeps |
| Carried by a faction mate, or in a mate's base bed | Stored where they are; on return they are put down beside the carrier or stay in the bed | |
| Dead | Stay dead; bodies follow the game's corpse rules | |
| A trade, conversation, container or held item was open | Closed; the held item goes back (`World.ReturnHeldItem`, `Sim/World.Interface.cs:122`) | |

While stored: no hunger, no healing, no ageing, no travel, no fights, not a raid target, not on anyone's map except faction mates'.
`offline.healing: off` by default: a logout must not be a free hospital. On reconnect the squads are re-made where they were, once the
zone's navmesh is live, on the nearest walkable point if their spot is now blocked (the existing hand-off, docs/simulation.md:472-475).
A reconnect within the delay just takes the characters back.

A player whose whole faction is offline for longer than `offline.expireDays` (default: never) can be archived by an admin: squads kept in
the save, buildings left as ruins or kept.

### Bases

A base is buildings with what they hold; it stays. "Unattended" below means **no member of the owning faction is online**; while any
member is online the base behaves as a base does in single-player when its owner is elsewhere.

| Aspect | Unattended base | Rule |
|---|---|---|
| Production (farms, machines, generators) | Stops; no catch-up for the time away | `offline.baseProduction`: `off` (default), `on`. For online factions the existing `UnloadedProduction` switch decides, as today (`Sim/Production/ProductionSystem.cs:55,1318`) |
| Raids by NPC factions | None: a campaign targets a player town only while one of that faction's characters stands in it (`Sim/Population/PopulationCampaigns.cs:732-754`), and stored characters are not in the world. A campaign already marching when the last member leaves is turned back | follows from the code |
| Wandering NPCs, animals | As for any building: they can walk in; doors stay as left | |
| Other players: damage, lockpicking, looting, dismantling | Not possible: buildings take no damage, locks cannot be picked, containers and machines of the faction do not open for others, turrets do not fire | `offline.baseProtection`: `full` (default), `lootOnly` (buildings safe, unlocked containers open), `none` |
| When protection starts | Not at once: only after the logout delay, and not while a hostile player's character is inside the town radius; it starts 5 minutes after the last one leaves | Logging off must not save a base that is being stormed |
| When it ends | When a member reconnects, after their characters are back | |
| Hired mercenaries, prisoners in the base's cages | Contracts pause; prisoners follow `offline.captives` | |

A faction with several members keeps its base alive while any of them is online, wherever their characters are.

## What must not change in single-player

- The settings pages, their defaults and where the values are stored.
- A single-player save keeps exactly today's files and keys: `meitou-server/` is written only when there is a second player or a runtime
  faction. Loaded hashes are compared as today (carried fields, repeatable loads), not against captured ones.
- Existing code mods load without a `side` field and behave as now.

## Not checked

- `ModHost` internals; whether mods in the wild define their own `SimCommand` types or read `World` from UI code against the rules.
- Save keys of map knowledge (`Towns`, `Camps`) were not traced.
- What the original does with a platoon whose faction it does not know.
- Whether a campaign can be "turned back" cleanly mid-march (`Redirect` exists, docs/simulation.md:530).
