# D1: Simulation multiplayer core (`src/Meitou.Simulation`): implementation spec

Host-authoritative co-op. Every human is a **player number** (0 = host or single-player, 1..N = remote). Faction state stays shared; per-human state moves to a **session**; squads get an **owner**; commands carry an **issuer**.

**Single-player identity invariant.** With one session (0), every squad owned by 0 and every command from player 0, each changed path must reduce to today's code. The golden pins in `GoldenHashTests` must not move. New hash terms are added only when a value is non-default (`Owner != 0`, sessions beyond 0, dialogue sessions beyond 0), in the same style as `Squads.cs:113`.

## 1. New and changed types (WP1 unless marked)

```csharp
// Commands.cs: SimCommand gains the issuer (an init property, so no constructor changes).
public abstract record SimCommand
{
    public required long Tick { get; init; }
    /// <summary>The player who issued it (0: the host / single-player; the host's CommandGate stamps remote ones). Deferred commands keep 0 unless a system sets it.</summary>
    public int Player { get; init; }
}
/// <summary>Host only (CommandGate refuses it from clients): player <see cref="SimCommand.Player"/> joins. Their start squad is made near Near (StartId: a NEW_GAME_STARTOFF; null: the Wanderer start), or they get their squads back when they still have living members.</summary>
public sealed record JoinPlayer(string? StartId, Vector3 Near) : SimCommand;          // handled in WP5
// SquadManagement.cs
/// <summary>Host only: a player squad passes to player Owner (an absent player's squads, a reloaded save).</summary>
public sealed record TransferSquad(int Squad, int Owner) : SquadCommand;              // handled in WP5

// PlayerSession.cs (new)
/// <summary>One human's view and windows (docs/simulation.md "Multiplayer"). Only Selection and Squad are hashed (session 0 in PlayerState's legacy position).</summary>
public sealed class PlayerSession
{
    internal PlayerSession(int number);
    public int Number { get; }
    public Guid Guid { get; set; }                       // roster: set by the host on join and by SaveLoader. Not hashed.
    public string Name { get; set; } = "";
    public List<CharacterId> Selection { get; } = [];
    public int Squad { get; set; } = -1;                 // the active squad, one of PlayerSquads(world, Number)
    public bool InCreation { get; set; }
    public IReadOnlyList<string> CreationRaces { get; set; } = [];
    public CharacterId Inspected { get; set; } = CharacterId.None;
    public string? InspectedBuilding { get; set; }
    public string? OpenContainer { get; set; }  public CharacterId ContainerOpener { get; set; } = CharacterId.None;
    public string? OpeningContainer { get; set; } public CharacterId ContainerWalker { get; set; } = CharacterId.None;
    public string? DoorTarget { get; set; }     public CharacterId DoorWalker { get; set; } = CharacterId.None;
    /// <summary>The camera focus (FocusCommand). Zones use it only while no player character exists; not hashed.</summary>
    public Vector3? Focus { get; set; }
    /// <summary>The last prospecting this player's character finished (Volatile read/write: the host reads it between ticks).</summary>
    public Production.ProspectReport? Prospected { get; internal set; }
    /// <summary>An editor a conversation opened for this player. The EditCharacter it allows takes it. Not hashed: ingress bookkeeping.</summary>
    public EditGrant? Grant { get; internal set; }
    internal bool TakeGrant(Editing.EditKind kind, CharacterId character, CharacterId surgeon);   // true and clears it when it matches
    internal void Prune(World world);
    internal void Hash(ref StateHasher h);   // h.Add(0x50536E00 + Number); h.Add(Squad); h.Add(Selection.Count); each id
}
public sealed record EditGrant(Editing.EditKind Kind, CharacterId Character, CharacterId Surgeon);
/// <summary>A system that holds an NPC for one player's window (a conversation or its approach, a trade).</summary>
public interface INpcHolder { int HolderOf(CharacterId npc); }   // the player number, -1 none
public readonly record struct PlayerMessage(int Player, string Text)
{
    public const int Everyone = -1;
    public bool For(int player) => Player < 0 || Player == player;
}
```

**`PlayerState`** keeps the shared faction state: `Faction`, `Exists`, `Money`, `Towns`, `Know`, `Camps`, `KnowCamp`, `SquadAi`, `SeeDiscoveries` (shared by design: one player closing the map clears the Recent marks for everyone, an engine choice). It loses every per-human member and adds:

```csharp
public PlayerSession Local => sessions[0];                    // always exists
public IReadOnlyList<PlayerSession> Sessions => sessions;     // ascending Number; [0].Number == 0
public PlayerSession? Find(int player);
public PlayerSession Session(int player);                     // get or add (player >= 0); only commands and Join call it
public PlayerSession Join(int player, Guid guid, string name);// add or update the roster entry
public PlayerSession? FindByGuid(Guid guid);
public int NextNumber();                                      // max Number + 1
public bool Inspects(CharacterId id);                         // any session's Inspected (BodySystem's parallel read)
internal void Prune(World world);                             // each session
internal void Hash(ref StateHasher h);
```

`Hash` must be exactly: `Faction, Money, Local.Squad, [SquadAi term], Local.Selection.Count, ids`, then `for k >= 1: sessions[k].Hash(ref h)`. Sessions are changed only in serial phases or on the host thread between ticks, so parallel phases can read the list safely.

**Why `Local`, no forwarders.** The ~70 shared references stay. Removing the per-human members makes every per-human use a compile error, so none is missed. In the simulation they become `world.SessionOf(command)` or a loop over the sessions. In Game and the tests (~30) they become `world.Player.Local.X`.

**`Squad.Owner`** (`Squads.cs`): `public int Owner { get; set; }`, the player number. It is 0 for NPC squads, where it means nothing. Hash it right after the `Index` term: `if (s.Owner != 0) hasher.Add(0x4F776E00 + s.Owner);`.

**World** (`World.cs`):
```csharp
public PlayerSession SessionOf(SimCommand command) => Player.Session(command.Player);
/// <summary>The player who owns the character: its squad's Owner (0 when it has no squad); -1 for an NPC or a gone id.</summary>
public int OwnerOf(CharacterId id);
internal int OwnerOfSlot(int slot);
/// <summary>The listed characters, or the issuer's selection when the list is empty (the 12 fallback sites).</summary>
public IReadOnlyList<CharacterId> Actors(SimCommand command, IReadOnlyList<CharacterId> listed);
public void Post(string message);                 // everyone (unchanged signature)
public void Post(int player, string message);     // one player; player < 0: everyone
public void PostOwner(CharacterId who, string message) => Post(OwnerOf(who), message);   // an NPC's goes to everyone
public IReadOnlyList<PlayerMessage> TakeMessages(); // return type changes from IReadOnlyList<string>
internal int HolderOf(CharacterId npc);           // first INpcHolder answer >= 0, else -1 (holders collected like `trades`)
public ProspectReport? Prospected => Player.Local.Prospected;   // was a volatile field with a setter
```
`RunTick` and `PlayerSystem.SlowWorld` call `Player.Prune(this)`.

**Interface seams** (WP1 adds them so later packages stay disjoint). These are default members, so test stubs still compile:
```csharp
// ITradeSource
TradeView? PublishTrade(int player) => player == 0 ? PublishTrade() : null;
// IDialogueSource
ConversationView? PublishConversation(int player) => player == 0 ? PublishConversation() : null;
DialogueRequest? PublishRequest(int player) => player == 0 ? PublishRequest() : null;
```

## 2. Issuer-aware handlers (WP1)

**Selection fallback (12 sites).** Each becomes `world.Actors(cmd, cmd.List)`. Delete the private `Who(World, ids)` helpers (CarrySystem.cs:107, ConstructionSystem.cs:131, FirstAidSystem.cs:88, LockSystem.cs:26, ProductionSystem.cs:160) and pass the command at every call: CarrySystem 120/125/132/147, CarrySystem.StealthKo 74, ConstructionSystem 154/167, FirstAidSystem 51/57/63/76, LockSystem 38/50, LockSystem.Doors 96, LockSystem.Shackles 36/69, ProductionSystem 173, ProductionSystem.Jobs 21. The inline sites become `world.Actors(...)` too: MovementSystem.cs:111, Orders.cs:165/174, PlayerState.cs:157 (Stop), Containers.cs:30, PickUp.cs:25, ArrestSystem.Bounties.cs:57. The gate does **not** expand empty lists. A `SelectCommand` followed by `MoveOrder([])` in the same tick must move the new selection ("player before movement", StandardSystems.cs:112), and a session's selection holds only its own characters (invariant below).

**PlayerSystem.Inputs.** It uses `var s = world.SessionOf(command)` per command.
- `SelectCommand`: keep only ids with `IsPlayer && OwnerOfSlot(slot) == command.Player`. A squad switch also needs `theirs.Owner == s.Number`.
- `InspectCommand`, `InspectBuildingCommand`, `CloseContainer`, `FinishCreation` act on `s`. `SeenDiscoveries` and `SquadAi` stay shared.
- `ReadMap` posts to the issuer.

**Invariant:** `s.Selection` only ever holds characters with owner `s.Number`.

**Prune (per session).** Drop gone ids. If the session had a selection and none of it is alive, select the first living `IsPlayer` character whose `OwnerOfSlot == Number`, so a session never selects another human's character. Clear `Inspected` when its character is gone.

**SquadManagement** (`Own(world, owner, id)` adds `s.Owner == owner`; Apply uses `owner = command.Player`):
```csharp
public static List<Squad> PlayerSquads(World world);             // every player squad, by (Owner, Index, Id); saves, FactionHeads
public static List<Squad> PlayerSquads(World world, int owner);  // one player's, by (Index, Id)
public static Squad NewSquad(World world, GameDatabase db, Vector3 at, string factionName, int owner = 0);
internal static void KeepActive(World world);                    // each session: keep Squad if Own(world, Number, Squad), else first with members, else first, else -1
```
- **NewSquad** picks the unique `Nameless_n` name across all player squads, takes `Index` = the owner's last + 1 and sets `Owner = owner`.
- **Per owner:** the `max squads` limit (an engine choice: each human has their own squads screen), "at least one squad", Reorder/Renumber, `NextSquad`, Active, Rename, Remove, and Move (source and target squads both `Own(owner)`).
- **Faction-wide:** `max faction size` (`FactionHeads`) and the dismiss head check.
- **Dismiss** removes the id from every session's selection.

**Per-session walks.**
- `OpenContainerInput` and `CloseContainer` use `SessionOf`. `ContainerWorkers` loops over `Player.Sessions`.
- `DoorOrder` uses `SessionOf(order)`. `DoorWalkers` loops over the sessions.
- Two players may open the same container: moves are validated by `InventoryItemToken` equality. `ContainerFor` does not change.

**BodySystem.cs:82**: `cold.IsPlayer || world.Player.Inspects(id)`.

**CharacterEditing.Apply.**
- The character must have `OwnerOfSlot(slot) == edit.Player`.
- Creation reads `SessionOf(edit).InCreation` and `.CreationRaces`.
- Surgery uses `bool paid = edit.Paid && (edit.Player == 0 || s.TakeGrant(EditKind.Surgery, edit.Character, edit.Surgeon))`.
- Recruit with `edit.Player != 0` requires `s.TakeGrant(EditKind.Recruit, edit.Character, CharacterId.None)`; otherwise post "That character cannot be edited.".
- Every post goes to `edit.Player`.

**PayBail** (ArrestSystem.Bounties.cs:91): only payers with `OwnerOfSlot(s) == order.Player` count.

**Prospecting** (ProductionSystem.Prospecting.cs:79): `var s = world.Player.Session(Math.Max(0, world.OwnerOfSlot(slot))); s.Prospected = new ProspectReport((s.Prospected?.Serial ?? 0) + 1, ...)`.

**Focus.** Delete `PopulationSystem.focus` and the unused `Focus` property.
- In `Inputs`: `FocusCommand f` sets `world.SessionOf(f).Focus = f.Position`.
- `PrepareLoading`: `world.Player.Local.Focus = cameraFocus`.
- `RefreshZones`: with no player characters, add every session's `Focus`, in Number order.
- `TryAnnounce` (PopulationCampaigns.cs:976): announce when any session's Focus is within `AnnounceRange`.

**Minimal compile fixes, refined later.**
- Use `Local` in: DialogueSystem.Recruit (WP2), PopulationSystem.Player and Sandbox (WP5), SaveLoader and SaveCapture (WP6), WorldPublish (WP4).
- Game: GameHost.DebugMenu, Sandbox, Saving, PlayerInterface.Editor use `Local`. GameHost.Interactive.cs:662 and Screenshot.cs:74 show only `m.For(0)` messages (`m.Text`).
- Tests: rename to `Local` in JailSaveTests, QuickSaveTests, SaveWorldTests, FirstAidTests, PlayerTests, FaithfulInteractionTests and TradeUiTests. AggroTests uses `TakeMessages().Select(m => m.Text)`.

**Message addressing** (WP1 does every file except Dialogue*/Trade*, which WP2 does). The rule: the answer to a command goes to its issuer; news about one player's character goes to its owner (`PostOwner`); faction or world news goes to everyone.
- **Issuer:** ConstructionSystem 164/174/210/216; ArrestSystem.Bounties 87/97/98/105/119; DebugCommands 84/85; CharacterEditing (all); Containers 40/45/93; LockSystem 54; LockSystem.Doors 112; LockSystem.Shackles 42/78; PlayerState 171/174/176/201/253/256; ProductionSystem 179/677/682/731/795; SquadManagement 109/118/119/234.
- **Owner:** CarrySystem 447 (the looter); ConstructionSystem 746; ArrestSystem.Bounties 142/155; PickUp 67; LockSystem 53/100/101/102; LockSystem.Doors 139/220; LockSystem.Shackles 146/148/155/156/160/166/175.
- **Everyone (unchanged):** CarrySystem 448, ConstructionSystem 639, the bounty rewards (183/203), arrests, crimes, kidnaps, fixtures, campaigns, pools, "Discovered", research complete (628), Relations.cs.

## 3. Dialogue and trade sessions (WP2: `Dialogue/DialogueSystem*.cs`, `Trading/TradeSystem.cs`)

**DialogueSystem.** Replace the fields `talker`, `target`, `approachUntil`, `open`, `view` and `request` with
```csharp
sealed class PlayerTalk { public required int Player; public CharacterId Talker = CharacterId.None, Target = CharacterId.None; public long ApproachUntil;
    public Conversation? Open; public ConversationView? View; public DialogueRequest? Request; public bool Empty => Talker.IsNone && Target.IsNone && Open is null; }
readonly SortedDictionary<int, PlayerTalk> talks = [];
PlayerTalk TalkOf(int player);   // get or add
```
`Conversation` gains `public int Owner`. A player conversation takes its session's number; `Fire` uses `Math.Max(0, world.OwnerOf(whom))`; `Announce` uses 0.

**Inputs.**
- `TalkOrder t`: if `world.HolderOf(t.Target) is >= 0 and var h && h != t.Player`, post "{name} is busy with someone else." to `t.Player` and keep the session as it is. Otherwise `End(world, TalkOf(t.Player))` and then `StartApproach`.
- `DialogueChoice` and `EndConversation` act on `talks[cmd.Player]` (TryGetValue: never create a session).

**SlowWorld** runs the approach logic for each talk in key order. `RefreshView` refreshes every talk. `End`, `CancelApproach`, `Start` and `StartApproach` take the `PlayerTalk`.

**Effects.**
- The Trade effect defers `OpenTrade(target, me) { Tick = world.Tick + 1, Player = c.Owner }`.
- DA_CHARACTER_EDITOR sets `TalkOf(c.Owner).Request` and `world.Player.Session(c.Owner).Grant = new(EditKind.Surgery, target, me)`.
- Recruit uses `world.Player.Session(c.Owner)` for its squad and selection fallbacks, `NewSquad(..., owner: c.Owner)`, `Request` on that talk, and `Grant = new(EditKind.Recruit, me, CharacterId.None)`.
- Posts: "has nothing to say" and "little full" go to `c.Owner`; relation and payment notices go to everyone (money is shared).

**OneAtATime** (Choice.cs:128) tests every talk's `Open`.

`PublishConversation(int)` and `PublishRequest(int)` read `talks`. The parameterless versions mean player 0. Implements `INpcHolder`: the first talk whose `Target == npc || Open?.Npc == npc`.

**Hash.** Return early when all the old collections are empty and every talk is `Empty`. Hash the player-0 talk exactly where `talker`/`target`/`open` were hashed (`CharacterId.None` twice when there is none). After it, for each talk with key != 0 that is not `Empty`, add `0x54616C00 + key`, then Talker, Target and the open-conversation terms.

**Two players talking to the same NPC: refused.** The first holder keeps it. Different NPCs work at the same time, and `Engaged` is never released early because two sessions cannot hold one NPC.

**TradeSystem.** Replace `customer`, `trader`, `open`, `traderWandered`, `approachUntil` and `view` with a `sealed class Deal { int Player; CharacterId Customer, Trader; bool Open, TraderWandered; long ApproachUntil; TradeView? View; }` held in a `SortedDictionary<int, Deal>`.
- `OpenTrade` checks `HolderOf(o.Trader)` first, as `TalkOrder` does, then closes and reopens its own deal.
- `Buy`, `Sell`, `ArrangeStock` and `CloseTrade` use `deals.TryGetValue(cmd.Player)`; `Resolve`, `Hold`, `Close` and `InReach` take the `Deal`.
- SlowWorld and RefreshView loop over the deals.
- Every Buy and Sell post goes to `deal.Player`. The enemy-attack branch is unchanged.
- `PublishTrade(int)` and `INpcHolder` (by `Trader`). The trade stays unhashed.

## 4. CommandGate (WP3: new `CommandGate.cs`)

```csharp
public sealed record GateOptions
{
    public bool Cheats { get; init; }                                          // lobby option: Debug* from clients
    public Func<Construction.PlannedBuilding, bool>? Placeable { get; init; }  // the host's geometric half (ConstructionSystem.cs:33)
    public int MaxList { get; init; } = 256; public int MaxPlans { get; init; } = 64; public int MaxText { get; init; } = 256;
}
public readonly record struct GateResult(SimCommand? Command, string? Refusal);
public static class CommandGate
{
    /// <summary>Host thread, at a tick boundary, before Commands.Enqueue. Returns the command restamped `with { Player = player, Tick = world.Tick }` (possibly filtered or stripped), or a refusal.</summary>
    public static GateResult Admit(World world, int player, SimCommand command, GateOptions? options = null);
    /// <summary>Every concrete SimCommand type has a rule here (the test enumerates the assembly).</summary>
    public static IReadOnlySet<Type> Ruled { get; }
    public static bool HostOnly(Type type);
}
```
`Admit` is one switch over the command type and **refuses by default**.

**Common checks:**
- `world.Player.Find(player)` is not null and `world.Player.Exists`.
- Lists are at most `MaxList`; strings at most `MaxText`.
- Every float, `Vector2`, `Vector3` and `Quaternion` is finite: a NaN would poison the run.
- `Owned(id)` means `world.OwnerOf(id) == player`.
- **Actors(list):** an empty list stays empty (the session's selection is owned by invariant). A non-empty list is filtered to owned ids; if nothing is left, refuse "not your characters".
- `PlayerSite(s)` means `world.Buildings.Find(s) is { Owner: var o } && o == world.Player.Faction`.

| Rule | Commands |
|---|---|
| **Host only (refuse)** | RepathCommand, SandboxMovement, StartContract, JoinPlayer, TransferSquad; every DebugCommand unless `Cheats` (then character lists filtered by Owned, `DebugGiveItem.Character` Owned) |
| Stamp only (window, view or shared faction state) | SeenDiscoveriesCommand, SquadAiCommand (mask `Option &= Defaults`), InspectCommand (target resolvable or None), InspectBuildingCommand, CloseContainer, CloseTrade, ArrangeStock, EndConversation, FinishCreation, DialogueChoice (`Index >= 0`), FocusCommand (finite), QueueResearch/CancelResearch (non-empty), ToggleDoorsOrder/ToggleDoorLocksOrder (the system filters by faction) |
| Actors only | MoveOrder (finite Target), StopCommand, OrdersCommand, RunSpeedCommand, PutDownOrder, RescueJobCommand, RestOrder, MedicJobCommand, GetOutOrder, PutInCageOrder |
| Actors + target resolvable | PickUpOrder, LootOrder, StealthKoOrder, TreatOrder, SplintOrder (Patient), PickLockOrder (Prisoner), PutShacklesOrder (Wearer), ShackleOrder (Wearer, `Enum.IsDefined(Work)`) |
| SelectCommand | ids filtered by Owned; refuse when that leaves none |
| AttackOrder | Attackers non-empty and filtered by Owned (no fallback exists); Target resolvable; **`Defensive = false`** |
| Actors + place | DoorOrder (`Fixtures.Find(Door)` is a door), OpenContainerOrder (fixture with `Contents`), PickUpItemOrder (`GroundItems.Find(Item)`), OperateOrder and AddJob (`Enum.IsDefined(Type)`; the site is a Machine or a PlayerSite), BuildOrder and DismantleOrder (PlayerSite) |
| One owned character | TalkOrder (Target resolvable and not `IsPlayer`), OpenTrade (Trader resolvable and not `IsPlayer`), MoveInventoryItem, ArrangeInventory, ReadMapItem, RemoveJob, MoveJob, ProspectOrder, DismissCommand, PutInMachine/TakeFromMachine (Site is a `Machines.Find` or a fixture with `Contents`) |
| Trade | BuyItem/SellItem: `Quantity >= 1`, token Quality finite, `Section` null, `main` or `backpack_content` |
| Sites | CancelSite and PauseSite (PlayerSite); QueueCraft (`Machines.Find(Site)`, `Count` clamped to 1..999), CancelCraft, RepeatCrafts (`Machines.Find`) |
| PlaceBuildings | `Plans.Count` 1..MaxPlans; each plan finite, `Floor` 0..16, `Parent` null or `Buildings.Find`, and `Placeable?.Invoke(plan) != false` |
| Squads | NewSquadCommand (finite At); Rename, Remove, Reorder (squad is a player squad with `Owner == player`); ActiveSquadCommand (-1, or owned); MoveToSquadCommand (Character Owned and target squad owned) |
| EditCharacter | Character Owned; Appearance non-null; Name at most `MaxText`; Kind defined. Surgery: Surgeon resolvable, and **`Paid` is stripped** unless `session.Grant == (Surgery, Character, Surgeon)` (peek only; the simulation takes the grant). Recruit: refuse unless the grant matches. Creation: the simulation checks `InCreation` |
| PayBailOrder | Prisoner resolvable (the simulation picks the issuer's payer) |

Every gated command gets its `Player` overwritten, so a client cannot speak for another player's session, focus or window. Late or early `Tick` values are restamped. The gate only reads the world: it never creates sessions or consumes grants.

## 5. Per-viewer publish (WP4: `WorldPublish.cs`, `WorldSnapshot.cs`, `World.Interface.cs`, `World.Loading.cs`, plus a stamp in `World.cs`)

**Split `Publish`:**
```csharp
// World (WorldPublish.cs)
CharacterSnapshot[] core = []; int[] coreIndex = [];   // slot -> index in core, -1 none
WorldSnapshot coreWorld = WorldSnapshot.Empty;         // the viewer-independent world parts, Characters = core
long stateStamp, coreStamp = -1;                       // stateStamp++ at RunTick start, in ApplyPausedInterfaceCommands (due > 0) and in PreparePopulation
void PublishCore(bool heal);                           // the old loop without the overlay fields; PublishHeal only when heal
WorldSnapshot ViewOf(PlayerSession? viewer, int number, bool talkable);
void Publish() { PublishCore(heal: true); PreviousSnapshot = Snapshot; Snapshot = ViewOf(Player.Local, 0, talkable: false); }
/// <summary>The snapshot one player sees, between ticks. Never runs PublishHeal, never creates a session, never changes hashed state. Reuses the last core unless the state changed since (stamp), then builds one without heal.</summary>
public WorldSnapshot PublishFor(int player);
```
Precondition: `Characters.Phase == Outside`. An unknown player gets an empty overlay.

**Character fields.**
- Overlay fields, applied with `core[i] with { ... }` only to the characters that need them: `Selected`, `Inspected`, `Skills`, `Inventory`, `AllSkills`, `SkillXpMultipliers`, `Hud`, `HealthParts`, `ItemGrid`, `Bounties`, `Path`, `CarriesShackles` (the viewer's selected or inspected characters), and `StealthKoChance` (`koSlot` from the viewer's selection; only NPCs that `CanBeKnockedOut`).
- Everything else is core, plus a new **`CharacterSnapshot.Owner { get; init; } = -1`**: `OwnerOfSlot(i)` for `IsPlayer`, else -1.

**World overlay.**
- `Viewer`.
- `Trade = trades?.PublishTrade(n)`, `Conversation` and `DialogueRequest` likewise.
- `PlayerSquads(this, n)`, `ActiveSquad = s.Squad`, `InspectedBuilding = Fixtures.DetailOf(s.InspectedBuilding)`.
- `Machines` = the shared list plus the viewer's `OpenContainer` view.
- `Prospected`, `InCreation`, `CreationRaces`.
- `Talkable`, only when `talkable` (from `PublishFor`).
- The per-tick host `Snapshot` leaves `Talkable` empty. The host UI keeps calling `World.CanTalk` live, so the golden scenarios that publish never run extra dry runs.

**Shared fields.**
- Unchanged: Projectiles, Speech, Towns, Camps, SquadAi, Buildings (+Revision), GroundItems (+Revision), Containers, DoorPicks, UnlockedBuildings, Doors, MachinesVersion, Research, Crafts.
- New: `PlayerFaction`, `Money`, and `Relations` with `RelationsRevision`.

**New `WorldSnapshot` init properties:**
```csharp
public int Viewer { get; init; }
public int PlayerFaction { get; init; } = -1;
public long Money { get; init; }
/// <summary>The player faction's relation towards each faction (FactionRelations.Get(PlayerFaction, f)); the hostility and ally tests are the public thresholds.</summary>
public IReadOnlyList<float> Relations { get; init; } = [];
public long RelationsRevision { get; init; }          // world-local; bumped when the row is rebuilt with other values
public IReadOnlySet<CharacterId> Talkable { get; init; } = NoIds;
public Production.ProspectReport? Prospected { get; init; }
public bool InCreation { get; init; }
public IReadOnlyList<string> CreationRaces { get; init; } = [];
```

**Relations.** The row is rebuilt only when `(data.Relations.Changes, Player.Faction)` differs from the cached key. `data` is the `PopulationData` of the PopulationSystem, else of the PlayerSystem.

**Talkable** (the decision for `CanTalk`).
- The talker is the first selected character of the viewer that resolves, `IsPlayer` and awake.
- The set holds the NPCs within `TalkableRange = 1500` (XZ, from `Previous`) of the talker, nearest first, at most `TalkableMax = 64`, for which `dialogues.CanTalk(this, slot, talker)` holds.
- It is cached per player in a `Dictionary<int, (long Tick, CharacterId Talker, IReadOnlySet<CharacterId> Ids)>` for `TalkableEveryTicks = 15` ticks while the talker is the same.
- CanTalk's dry run puts `rolls` and `permanent` back (DialogueSystem.cs:251-254). A test asserts `StateHash` is unchanged by `PublishFor`.

Seed, clock, pause and speed stay out of the snapshot (handshake and session clock).

## 6. Joining (WP5: `PopulationSystem.Player.cs`, `PopulationSystem.cs`, `SquadManagement.cs` TransferSquad case)

```csharp
/// <summary>Adds player `player`'s start squad near `near` without touching Faction, Money or Research. Before the first tick, or from JoinPlayer.</summary>
public Squad AddPlayer(World world, int player, NewGameStart start, Vector3 near);
```

**Refactor** the member building of `StartPlayer` into `BuiltSquad BuildStart(World, NewGameStart, ulong key, Vector2 at)`. StartPlayer must keep exactly the same key, RNG calls and `SpawnSquad` order. `SpawnSquad` gains `int owner = 0`, and its Squad initializer sets `Owner = owner`.

**AddPlayer:**
1. Throw (direct call) or post to the player (command) when `!world.Player.Exists` or `player <= 0`.
2. **Rejoin:** if `PlayerSquads(world, player)` holds a living member, make no new squad. Run `KeepActive`, and if the selection is empty, select the first member of the active squad; then return.
3. Compute `key = Rng.Mix(SeedSalts.PlayerStart ^ Rng.StableHash(start.Id) ^ Rng.Mix((ulong)player << 32 ^ (ulong)world.Tick))`.
4. Place it at the first walkable `Rng.PointInDisc(roll, near.XZ, JoinRadius = 150, 3)` of 20 attempts (else `near`). `ForceStartPos` is ignored for joiners.
5. Call `SpawnSquad(..., player: true, owner: player)`. Name it with NewSquad's unique `Nameless_n` rule; `Index` is 0.
6. Set up the session: `s = world.Player.Session(player)`, then `s.Squad = squad.Id`, `s.Selection = [first]`, `s.InCreation = true`, `s.CreationRaces = start.ForceRace` ids, `s.Focus = near`.
7. The shared purse gets nothing (engine choice). The faction head cap is not checked for joiners (engine choice).

**JoinPlayer handler** (`PopulationSystem.Inputs`): the start is `data.Db.Find(StartId)` of type NEW_GAME_STARTOFF, else the start named `NewGameStart.DefaultName`, else the first one. Then `AddPlayer(world, join.Player, start, join.Near)`. It runs in the Inputs phase, where spawning is allowed (`CharacterTable.Spawn` forbids only Parallel).

**TransferSquad** (in `SquadManagement.Apply`): the squad must be a player squad and `world.Player.Find(Owner)` must not be null. Set `squad.Owner`, remove its members from the other sessions' selections, then `KeepActive`.

A multiplayer new game calls `StartPlayer`, then `AddPlayer(world, n, start, leaderPosition)` for each lobby player, before tick 0. A join in progress enqueues `JoinPlayer { Player = n }`.

## 7. Saves (WP6: `Saving/SaveCapture.cs`, `Saving/SaveLoader.cs`, `docs/formats/save.md`)

**Keys** (Meitou-only, ignored by the original, like `meitou seed`):
- PLATOON (player platoon) string `meitou owner`: the owner's GUID ("D" format).
- CAMERA int `meitou players`, plus per entry k: string `meitou player<k> id`, string `meitou player<k> name`, int `meitou player<k> number`.

**Capture.**
- Always remove any stale `meitou player*` keys from CAMERA and `meitou owner` from the written player platoons.
- If some session has `Guid != Guid.Empty`, write the roster: every session with a GUID, in Number order. Each player squad then gets `meitou owner` = its owner session's GUID; leave it out when the owner has no GUID.
- Selection (`WriteSelection`) stays session 0's.
- Single-player saves are byte-for-byte as today.

**Load.** `SaveLoadOptions.LocalPlayer` is a `Guid` (Empty: a single-player load).
- **Empty:** ignore the roster and owner keys; every squad goes to 0 and no sessions are made.
- **Otherwise:** read the roster (skip bad or duplicate GUIDs).
  - The local GUID becomes number 0. Whoever held 0 takes the local entry's old number, or `NextNumber` when the local player was absent.
  - `Join` each other entry, and set `Local.Guid` and `Local.Name`.
  - `LoadPlayerPlatoon` gets `owner = roster[guid]`; an absent or unknown GUID means 0, with a `loaded.Notes` line for an unknown one.
  - The CAMERA selection loads into session 0, filtered to owner 0. The active squad for session 0 is as today but limited to `PlayerSquads(world, 0)`. Finish with `SquadManagement.KeepActive(world)`.
- A returning player's net handshake finds the player with `FindByGuid` and enqueues `JoinPlayer`, whose rejoin path selects their squad.

## 8. Leave alone

- `CommandQueue` order, `Defer` and the `Tick` semantics. Deferred commands never pass the gate.
- `IsPlayer` (it means the player faction), `FactionRelations.PlayerFaction`, `data.PlayerFaction`, AI and aggro rules.
- Research, Machines, building `Owner = Faction`, Towns and Camps: shared.
- The `ApplyPausedInterfaceCommands` filter list.
- GameHost pause, modal and RunTick-after-creation flows (Game and net modules).
- Golden pins: if one moves, the change is wrong.

## 9. Docs

`docs/simulation.md` gets `## Multiplayer (host-authoritative co-op)`, one subsection per package: sessions and ownership (WP1, with the engine choices: max squads per owner, shared SeenDiscoveries, joiners add no money and skip the head cap), dialogue and trade sessions (WP2), command gate (WP3), per-viewer snapshots (WP4), joining (WP5).

The "Player" paragraph now resolves empty lists against the issuer's session selection. Save keys go in `docs/formats/save.md` (WP6), with one line under "Saves".

## 10. Work packages

**WP1 Foundation** (first and alone; touches many files): §1 and §2.
- New `PlayerSession.cs`.
- Changed: `PlayerState.cs`, `Commands.cs`, `World.cs`, `World.Prospecting.cs`, `Squads.cs`, `SquadManagement.cs`, `MovementSystem.cs`, `Orders.cs`, `BodySystem.cs`, `Items/Containers.cs`, `Items/PickUp.cs`, `Carrying/CarrySystem*.cs`, `Construction/ConstructionSystem.cs`, `FirstAid/FirstAidSystem.cs`, `Locks/LockSystem*.cs`, `Production/ProductionSystem*.cs`, `Crime/ArrestSystem*.cs`, `DebugCommands.cs`, `Editing/CharacterEditing.cs`, `PopulationSystem.cs` (focus), `PopulationSystem.Loading.cs`, `PopulationCampaigns.cs`, `Trading/TradeSystem.cs` (interface default only), `Dialogue/DialogueSystem.cs` (interface default only) and `DialogueSystem.Recruit.cs` (`Local`).
- Also the `Local` compile fixes in WorldPublish, SaveCapture, SaveLoader, PopulationSystem.Player and Sandbox, and the Game and test renames.
- Tests: `tests/Meitou.Tests/Simulation/MultiplayerSessionTests.cs`.
  - Two sessions select independently.
  - An empty-list `MoveOrder` uses the issuer's selection.
  - Prune never picks another owner's character.
  - KeepActive and the squad limits act per owner.
  - `StateHash` changes only once a second session or a non-zero Owner exists.
  - PayBail picks the issuer's payer.
- Gate: the full test run, goldens unchanged.

**After WP1, in parallel** (disjoint files):

| WP | Files | Tests (approved) |
|---|---|---|
| WP2 dialogue and trade | `Dialogue/DialogueSystem*.cs`, `Trading/TradeSystem.cs` | `MultiplayerWindowsTests.cs`: two players talk and trade at once with different NPCs; the second to the same NPC is refused and the first is unaffected; the recruit joins the talking player's active squad; DA_CHARACTER_EDITOR grants only that player; containers are per session |
| WP3 gate | `CommandGate.cs` (new) | `CommandGateTests.cs`: every concrete `SimCommand` type is in `Ruled`; foreign ids are filtered or refused; host-only types are refused; `Defensive` and `Paid` are stripped (Paid kept with a matching grant); NaN is refused; unknown players are refused |
| WP4 publish | `WorldPublish.cs`, `WorldSnapshot.cs`, `World.Interface.cs`, `World.Loading.cs`, `World.cs` (stamp only) | `PublishForTests.cs`: `PublishFor(0)` equals `Snapshot` except `Talkable`; player 1's overlay holds only their selection; the heal floater count is unchanged by extra `PublishFor` calls; `StateHash` is unchanged by `PublishFor`; Owner is right on characters |
| WP5 join | `PopulationSystem.Player.cs`, `PopulationSystem.cs` (Inputs case), `SquadManagement.cs` (TransferSquad case) | `JoinTests.cs`: AddPlayer leaves Faction, Money and Research alone; the squad is owned by the joiner; that session is in creation; rejoin makes no squad; StartPlayer is unchanged (goldens) |
| WP6 saves | `Saving/SaveCapture.cs`, `Saving/SaveLoader.cs`, `docs/formats/save.md` | `MultiplayerSaveTests.cs`: owners round-trip through capture and load with the roster remap; a single-player save has no new keys; a load with an empty LocalPlayer gives every squad to 0 |

WP1 already changed `SquadManagement.cs`; WP5 only adds a `case`, and WP2 does not touch that file.

## 11. Cross-module interfaces

**Exposed** (to the net or host module and the UI module):
- `SimCommand.Player` (`int`, init).
- Commands `JoinPlayer(string? StartId, Vector3 Near)` and `TransferSquad(int Squad, int Owner)` (host only).
- `PlayerState`: `Local`, `Sessions`, `Find(int)`, `Session(int)`, `Join(int, Guid, string)`, `FindByGuid(Guid)`, `NextNumber()`, `Inspects(CharacterId)`.
- `PlayerSession` (public members of §1). `EditGrant`. `PlayerMessage`.
- `World`:
  - `SessionOf(SimCommand)`, `OwnerOf(CharacterId)` (address knockout events with it), `Actors(...)`.
  - `Post(int, string)`, `PostOwner(CharacterId, string)`, `TakeMessages(): IReadOnlyList<PlayerMessage>`.
  - `PublishFor(int player): WorldSnapshot`, `Prospected`.
- `WorldSnapshot.{Viewer, PlayerFaction, Money, Relations, RelationsRevision, Talkable, Prospected, InCreation, CreationRaces}`, `CharacterSnapshot.Owner`.
- `CommandGate.Admit(World, int, SimCommand, GateOptions?) : GateResult`, `CommandGate.Ruled`, `CommandGate.HostOnly(Type)`, `GateOptions`, `GateResult`.
- `PopulationSystem.AddPlayer(World, int, NewGameStart, Vector3) : Squad`.
- `Squad.Owner`, `SquadManagement.PlayerSquads(World, int)`, `NewSquad(..., int owner = 0)`.
- `SaveLoadOptions.LocalPlayer : Guid`.
- `ITradeSource.PublishTrade(int)`, `IDialogueSource.PublishConversation(int)` / `PublishRequest(int)`, `INpcHolder`.

**Consumed:**
- **Net or host module** (host thread, between ticks):
  - Numbers: `FindByGuid(g)?.Number ?? NextNumber()`, then `Player.Join(n, g, name)`; `JoinPlayer { Player = n }` on connect; `Local.Guid`/`Name` when hosting.
  - Each remote command: `CommandGate.Admit(world, n, cmd, options)` before `Commands.Enqueue`; refusals are logged and may be posted with `Post(n, ...)`.
  - Drains `TakeMessages()` once per frame: the host shows `For(0)`, clients get `For(n)`.
  - `PublishFor(n)` after each tick and after `ApplyPausedInterfaceCommands`; `World.Seed` and the data hash in the handshake.
  - No `RunTick` outside the session clock in multiplayer (GameHost.Editor.cs:47).
- **Game host:**
  - `GateOptions.Placeable` (the geometric placement check); `GateOptions.Cheats` (lobby).
  - `SaveLoadOptions.LocalPlayer`; `StartPlayer` + `AddPlayer` for a multiplayer new game.
  - Message display filter `m.For(0)`.
- **UI module:** a client reads `Money`, `PlayerFaction`, `Relations`, `Talkable`, `Prospected`, `InCreation`, `CreationRaces` and `Owner` from its snapshot, not from `World`, `popData` or `CanTalk`.
- **Data:** `NewGameStart.From`/`LoadAll`/`DefaultName`; `FactionRelations.Get`/`Changes`/thresholds.
