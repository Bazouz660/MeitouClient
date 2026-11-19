# 03. Commands and authority

How a player's wish reaches the world, who may do what, and what replaces pausing. Upstream `a1ef2e06f`.
`Sim/` = `src/Meitou.Simulation/`, `Game/` = `src/Meitou.Game/`.

## Today (facts)

- `SimCommand` is an abstract record with one member, `required long Tick` (`Sim/Commands.cs:12-18`). The sender stamps the tick itself,
  normally `Tick = Session.World.Tick` (268 such lines in Game). No issuer.
- `CommandQueue.Enqueue` works from any thread; `TakeDue` returns due commands by tick, then queue order (`Commands.cs:76-107`).
- There is no dispatcher: every system's `Inputs(world, due)` gets the whole due list and picks its own types
  (`Sim/World.cs:842-856`); a command nobody matches is dropped silently. 15 systems implement `Inputs`.
- **158 concrete command records** and 4 abstract bases (`OrderCommand`, `DebugCommand`, `SquadCommand`, `HeldCommand`) in 35 files.
- The systems themselves defer commands of the same types during a tick (`World.Defer`, `World.cs:1015`; 67 sites, e.g. `AttackOrder` 19,
  `DialogueEvent` 40). Deferred commands run before host commands of the same tick (:1035-1051).
- The UI sends through `PlayerInterface.Say` (`Game/PlayerInterface.cs:784-788`; 176 sites). 91 more sites call `Commands.Enqueue`
  directly, most in developer tools; code mods use `ModContext.Enqueue` (`Game/Mods/ModContext.cs:139`).
- Checks that exist are all against the one player: empty id list means the selection (`Sim/Movement/MovementSystem.cs:251,384,403`;
  `Sim/Combat/CombatSystem.cs:405-451`); some commands test `IsPlayer` or `Player.Faction`.
- Commands with **no check at all**: `MoveOrder` and `FollowOrder` order any id that resolves (`MovementSystem.cs:380-436`);
  `AttackOrder.Attackers` (`CombatSystem.cs:420-437`); `TreatOrder`, `SplintOrder`, `RestOrder` (`Sim/FirstAid/FirstAidSystem.cs:112-139`);
  `InspectCommand`, `ShowInventoryCommand` (`Sim/Player/PlayerSystem.cs:208-230`); `RotateItem`, commented "no check of whose inventory
  it is" (:238-246); every `DebugCommand`; `EditCharacter`. In single-player that is harmless. On a server it is the cheat surface.
- Time is not a command. Speed and pause are direct calls on the session: keys (`Game/Host/WorldSession.cs:581-586`), HUD buttons
  (`Game/PlayerInterface.Ui.cs:1417`), build mode (`Game/Building/BuildMode.cs:200,211`), photo mode (`Game/Host/Photo/PhotoMode.cs:550-618`),
  the character editor (`Game/Editor/CharacterEditorPanel.cs:535`), pause on load (`Game/Host/GameSaves.cs:573`), the console.
- While paused, a list of about 60 interface command types is applied between ticks by `World.ApplyPausedInterfaceCommands`
  (`Sim/World.Interface.cs:27-84`): orders, selection, squads, inventory, trade, dialogue choices, crafting, build placement. Modal UI
  asks for a pause (`UpdateMenuPause`, `PlayerInterface.Ui.cs:1107`).
- upstream docs already note that `World.CanOrder` "is a pure read ... Planned native multiplayer can call it on the host to gate guest
  commands" (docs/simulation.md:271).

## Target (proposal)

### The sender

```
public abstract record SimCommand
{
    public required long Tick { get; init; }
    public PlayerId Sender { get; init; }      // default: PlayerId.Local (player 0)
}
```

`Sender` has a default, so none of the existing construction sites changes. Rules:
- A command from the network gets `Sender` from its connection and `Tick` = the next tick, both set by the host. Whatever the client
  wrote is ignored.
- A command a system defers (`World.Defer`) has `Sender = PlayerId.World`; the gate does not apply to it. An AI's `AttackOrder` is not a
  player's.
- A code mod's `Enqueue` on the server is `PlayerId.World` unless the mod names a player.

### The gate

One place, `CommandGate.Admit(world, command)`, called from `World.TakeDue` before any system's `Inputs`. A refused command is removed
and its sender told why (a short code, shown as the existing notice line). Each command type declares what it needs; the declaration is
a table in one file (`Sim/CommandRules.cs`), so a reviewer sees all 158 at once and a test can assert that no type is missing.

| Class | Who may send | The gate checks | Types (count) |
|---|---|---|---|
| Orders to characters | any player | every subject id is a living character of a squad the sender owns; an empty list resolves to the sender's selection | `MoveOrder`, `FollowOrder`, `AttackOrder`, `TreatOrder`, ... (32) |
| Selection, inspect | any player | `Select`: own characters only. `Inspect`, `ShowInventory`: target within the sender's interest ([04](04-replication.md#interest)); another faction's inventory only where the game allows looking (downed, looting, trading) | 5 |
| Inventory, loot, containers, held item | any player | the acting character is the sender's; the source and target grids are ones that character may open now (own, open loot, open container, a faction mate who allows it) | 17 + `HeldCommand`s |
| Trade | any player | the sender has an open trade session and the named window is a character of theirs in range (`PassRange`, docs/simulation.md:1851) | 8 |
| Dialogue | any player | the sender owns the open conversation | 5 (`DialogueEvent` is world-only) |
| Squads | any player | the squads and characters are the sender's; limits per player | 15 |
| Building, construction | any player | the site's or town's owner is the sender's faction and the sender's role allows building | 12 |
| Production, research, crafting | any player | the machine's owner is the sender's faction; research is the faction's | 18 |
| Character editor | any player | only the sender's characters, and only while that player is in creation or the rule allows in-play editing | 3 |
| Host | host only | never accepted from a connection | `FocusCommand`, `RepathCommand`, `GateRegionsCommand`, `CameraViewCommand` (4) |
| Time | per rules, below | | new |
| Admin and debug | admins | sender's account is in the admin list; refused on a server with cheats off | 34 `Debug*`, `SandboxMovement`, `StartContract`, `EndContract`, `ChangeAi`, `VisitTown` |

Ownership tests need stage 1 of [02](02-players-and-factions.md#migration-path) (`Squad.Owner`). `World.CanOrder` stays what it is,
the test of whether an order *can run*; the gate answers whether the sender *may give* it.

`GateRegionsCommand` carries a live object (`Sim/Locks/GateRegions.cs:31`) and `FocusCommand` and `CameraViewCommand` are how the game
reports its camera. A remote client reports its camera with one small message, `ViewReport` (anchor position, frustum, shown
interiors), a few times a second; the host turns it into the player's view anchor.

### Rate limits

Per connection: a token bucket (default 60 commands a second, burst 120) and a size limit per command (64 KB; `PlaceBuildings` and
`EditCharacter` are the large ones). Over the limit the connection is warned, then dropped. Numbers are rules.

### Sessions per player

| Thing | Today | Proposal |
|---|---|---|
| Open trade | one `_customer`, `_trader`, `_session`, `_view` (`Sim/Trading/TradeSystem.cs:291-307`) | one session per player. Two players may trade with the same shop at once: every buy and sell is applied serially at a tick and checked against the stock as it is then; the loser of a race gets the existing "no longer there" refusal. Buy-back receipts match their own session (:1806) |
| Conversation | one `_open` (`Sim/Dialogue/DialogueSystem.cs:192`) | one per player. An NPC in a conversation is busy: a second talker waits at talk range and gets "busy" |
| Player-to-player trade | none | `CharacterTrading` (`Sim/Dialogue/CharacterTrading.cs`: `ConfirmCharacterTrade`) is the base: both sides place items and money, both confirm, the swap is one tick. Giving by drag onto another player's character asks that player |
| Open container, loot, door target and their walkers | one each in `PlayerState` :200-247 | they sit in `PlayerState`, which becomes per player |
| Held item (on the mouse) | one, hashed (`PlayerState.cs:253`) | per player. On disconnect it returns to where it came from (`World.ReturnHeldItem`, `World.Interface.cs:122`) |
| Two players at one container | impossible | both may open it; moves are serial; a move whose source cell changed is refused and the client's view corrects on the next update |
| `World.CloseInventories` (alarm, caught stealing) | closes all | closes the sessions of the player concerned |

### Latency

A command now lands one round trip later instead of one tick later. What the client does at once, without waiting:

| Action | Shown at once | Corrected by |
|---|---|---|
| Selection | the client's own highlight | the personal view |
| Move order | the order marker and sound | the characters start moving when the server says so (typically 50-150 ms) |
| Inventory drag | the item follows the mouse (the held item is drawn locally) | the personal view; a refused move snaps back |
| Menus, windows | open locally | their content arrives with the next personal view |

Nothing in the world is predicted: characters, fights and doors are shown as the server sent them. Kenshi is a game of orders, not of
reflexes, and an order already takes a tick or a tick-ahead batch to land today (docs/simulation.md:54).

### Time: speed, pause and votes

Speed becomes a command to the host: `SpeedRequest(level)` where level is pause, 1, 2 or 3 (the fast speeds are rules, today
`FastSpeed`, `FastestSpeed`). The host's `HostClock` decides. Rule `time.mode`:

| Mode | Behaviour | Default for |
|---|---|---|
| `local` | one player: every request applies at once. Exactly today. | single-player |
| `vote` | A request for a **lower** speed (including pause) applies at once: anyone can slow the game when they need to. A request for a **higher** speed applies when every connected player has asked for at least that speed (their "ready" speed, shown on the HUD beside their name), or after a majority has for `time.voteSeconds`. A player in a menu counts as asking for 1x. | hosting for friends |
| `host` | only admins change speed | optional |
| `fixed` | 1x always; requests are refused; nobody can pause | dedicated servers; the owner's rule for large servers |

Extra rules, all settings: `time.pauseAllowed` (off in `fixed`), `time.maxPauseSeconds` (a pause ends by itself; default 300),
`time.pauseWhenEmpty` (a server with nobody online stops the clock; default on for internal and co-op, so the world does not run away
while friends sleep; a dedicated server may keep running).

Ragdoll physics steps in real time and so depends on speed (`World.PhysicsSpeed`, docs/simulation.md:61): unchanged, the speed is the
server's.

### What replaces "the world is paused"

| Today | In multiplayer (any mode but `local`) |
|---|---|
| Interface commands applied while paused (`World.Interface.cs:27-84`) | While the server is paused (a vote passed), the same path runs for every player's due commands, so friends can sort inventory and give orders together in a pause. While it runs, the same commands simply apply at the next tick, as they already do today when the game is not paused |
| Menus and modal windows pause (`UpdateMenuPause`) | They do not. Opening the menu sets the player's ready speed to 1x. The settings and save menus say so |
| Shopping and inventory in a frozen world | Live. Trade already stays open "through fights and knock-outs" (docs/simulation.md:1855) |
| Dialogue | Live; the conversation holds only the NPC |
| New game: the world waits in `InCreation` (`PlayerState.cs:185`; `Sim/Population/PopulationSystem.cs:400`) | The joining player edits characters on the client against a private preview. `CreateSquad(start, edited characters)` is sent on confirm and the gate checks it against the start's limits. The world never waits |
| In-play character editor (plastic surgeon) | `EditCharacter` on own characters; the world runs |
| Pause on load (`GameSaves.cs:573`) | Local only |
| Loading pause (`LoadingPause`, off by default) | Never on a server: nobody's loading stops the world |
| Build mode pauses (`BuildMode.cs:200`) | Does not; placement is checked when the command lands, and a site that became blocked is refused with its text |
| Photo mode pauses and slows time (`PhotoMode.cs:550,562`) | Camera only; the world runs |
| Agent `step` (`WorldSession.cs:287-290`) | Local host and admins only |
| Autosave counts unpaused time (`GameSaves.cs:109`) | Unchanged, on the server |

### Developer tools

The console, the agent channel, the debug menu and the scenarios call `Commands.Enqueue` directly and read the `World`. They keep
working on a local host. On a remote client the debug menu shows only what works through admin commands, if the player is an admin.
The dedicated server's console is the same command set (`pause`, `speed`, `step`, state dumps) plus the admin commands of
[07](07-settings-mods-saves.md#administration).

## What must not change in single-player

- With one local player the gate admits everything it admits today: every character is player 0's. The no-check commands gain checks
  that a lone player always passes. **Risk:** tests and scenarios that order NPCs with `MoveOrder` (the no-check path). They send as
  `PlayerId.World`, which the gate skips; the test helpers get that default.
- `time.mode = local` is today's behaviour bit for bit, including the paused interface path and the menu pause.
- Tick stamping: the local session stamps `World.Tick` exactly as the 268 sites do now.

## Not checked

- The bodies of the production handlers (`Sim/Production/ProductionSystem.cs:407-438`) for owner tests.
- Whether every `HeldCommand` validates the grid it names.
- Mod-defined command types: how many existing mods define their own `SimCommand` records.
