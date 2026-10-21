# Wave 2 as built (merged into multiplayer). Read the sections for packages you consume.

## S2

### Summary
Package S2 (dialogue and trade per player) is done and committed. The worktree branch had been cut from master (8f26eca7), not from 'multiplayer'. HEAD was an ancestor of 'multiplayer' and the tree was clean, so I fast-forwarded it to multiplayer 36b3418d (wave 1) before starting.

DialogueSystem (src/Meitou.Simulation/Dialogue/DialogueSystem*.cs):
- The single talk state is replaced by one `PlayerTalk` per player, kept in a `SortedDictionary<int, PlayerTalk>`; `TalkOf(player)` gets or adds one.
- `Conversation.Owner` is the talk's player for a player conversation, `Math.Max(0, OwnerOf(whom))` in Fire, and 0 for announcements.
- `TalkOrder` is refused when another player holds the NPC (`world.HolderOf`): it posts "{name} is busy with someone else." to the issuer and changes nothing. Otherwise it ends the issuer's conversation and starts the approach. `DialogueChoice` and `EndConversation` look the talk up with TryGetValue and never create one.
- SlowWorld and RefreshView go over every talk in key order. The `one at a time` test checks every talk.
- Effects: Trade defers `OpenTrade` with `Player = c.Owner`. DA_CHARACTER_EDITOR sets that player's Request and a Surgery grant (patient, surgeon) on their session. Recruit uses `Session(c.Owner)` for the active squad and selection fallbacks, calls `NewSquad(..., c.Owner)` when the squad is full, and sets the owner's Request and a Recruit grant. "has nothing to say" and "a little full" go to the owner; relation and payment notices still go to everyone.
- `PublishConversation(int)` and `PublishRequest(int)` are implemented; the parameterless versions mean player 0. The class implements `INpcHolder`, which World already registers.
- Hash follows the D1 §3 layout: player 0's talk is hashed where the old state was (None twice when there is none), then each non-empty talk with key != 0 adds 0x54616C00 + key, Talker, Target and its conversation terms. The early return now checks that every talk is Empty.

TradeSystem (src/Meitou.Simulation/Trading/TradeSystem.cs):
- One `Deal` per player in a `SortedDictionary`.
- `OpenTrade` checks HolderOf first (same refusal message), then closes and reopens the issuer's deal. The enemy-attack branch is unchanged.
- Buy, Sell, ArrangeStock and CloseTrade act on the issuer's deal only. Resolve, Hold, Close and Publish take the deal.
- SlowWorld and RefreshView go over the deals. Every buy/sell notice goes to `deal.Player`.
- `PublishTrade(int)` is implemented, and the class is an `INpcHolder` (by trader). Deals are not hashed.

Docs: the docs/simulation.md subsection "Dialogue and trade per player" replaces the placeholder line; neighbouring subsections are untouched.

Tests: new tests/Meitou.Tests/Simulation/MultiplayerWindowsTests.cs with the 5 approved cases:
- two players talk and trade at once with different NPCs;
- a second player is refused an NPC the first holds (talk and trade), and the first is unaffected;
- the recruit joins the talking player's active squad (which is neither the talker's squad nor player 0's), with the request and grant for that player only;
- DA_CHARACTER_EDITOR grants only that player;
- containers are per session.

### Deviations
1. Deferred orders now carry the issuer. The MoveOrder and AttackOrder that a TalkOrder or OpenTrade defers (the walk to the NPC, or the attack on a hostile one) have `Player = issuer`; the spec only required this for OpenTrade from the Trade effect. Deferred commands are not hashed and their character list is explicit, so single-player behaviour and the goldens do not change. The point is that a later issuer-aware handler sees the right player.
2. The trade refusal reuses the exact dialogue text, "{name} is busy with someone else.", with the trader's name; the spec only says "as TalkOrder does".
3. Two older lines elsewhere in docs/simulation.md (the "Trade" section's "open trade" and the "Dialogue" State bullet's "open conversation") were left as they are, to respect the rule that parallel packages edit only their own subsection. They are still accurate per player, and the per-player facts are in the S2 subsection.
4. The worktree was fast-forwarded from master to 'multiplayer' 36b3418d before starting, because it had been created from master (see summary).

No files outside the package's list were touched, apart from the new test file the contract names.

### Review remaining
- Pre-existing and left alone so the goldens and single-player behaviour stay the same: when a TalkOrder replaces an approach that is still under way, the old target keeps Engaged = true, because StartApproach overwrites Target without CancelApproach. In multiplayer this no longer blocks other players, since HolderOf no longer names that NPC, but the NPC's AI stays held. This needs a later decision.
- Deviation 3: the older docs/simulation.md lines in the 'Trade' section and the 'Dialogue' State bullet still read in the singular. They stay accurate per player and the S2 subsection carries the per-player facts. Not changed, to respect subsection ownership.
- Full slow suite not run in this review.

## S3

### Summary
S3 Command gate is done. I added D:\Github\MeitouClient\.claude\worktrees\wf_f28ac482-87c-2\src\Meitou.Simulation\CommandGate.cs, which declares GateOptions, GateResult and CommandGate (Admit, Ruled, HostOnly) exactly as the contract §2b states them.
- Rules: there is one per concrete SimCommand type, 81 in all, kept in a FrozenDictionary that Ruled reads its keys from. Any type without a rule is refused.
- Order of checks: the player must be known (Player.Find, so no session is ever created) and Player.Exists must be true. Then host-only types are refused (RepathCommand, SandboxMovement, StartContract, JoinPlayer, TransferSquad), then DebugCommands unless Cheats is on, then the common field checks, then the per-command rule from D1 §4.
- Character lists: an empty list stays empty. A non-empty list is filtered to OwnerOf(id) == player and refused when nothing is left. SelectCommand and AttackOrder follow the same rule, and AttackOrder may not be empty.
- Per-command checks cover targets, places and values: living targets, door and container fixtures, ground items, machines or player-faction buildings, the sender's own squads, defined enums, trade sections, and PlaceBuildings limits including Floor 0..16, an existing Parent and the Placeable callback.
- Rewrites: AttackOrder.Defensive is cleared, SquadAiCommand.Option is masked to Defaults and QueueCraft.Count is clamped to 1..999. EditCharacter.Paid is cleared unless the session's Grant is exactly (Surgery, Character, Surgeon); the grant is only peeked, never consumed. A recruit edit is refused without its grant, and the look's floats and vectors must be finite.
- Every admitted command is restamped with { Player = player, Tick = world.Tick }.
- Docs: I replaced the "Command gate" placeholder in docs/simulation.md with the gate's current contract.
- Tests: D:\Github\MeitouClient\.claude\worktrees\wf_f28ac482-87c-2\tests\Meitou.Tests\Simulation\CommandGateTests.cs has the 6 approved cases: every concrete type is in Ruled (reflection over the assembly); foreign ids are filtered or refused and admitted commands are restamped; host-only types and cheats without the option are refused; Defensive and Paid are stripped, Paid is kept with a matching grant and the grant is not used up; NaN or infinity is refused, including inside a plan quaternion and an item token; an unknown player is refused and gets no session.

### Deviations
1) My worktree had been created from master (8f26eca7), not from 'multiplayer', so none of the wave-1 code was there. The branch had no commits and a clean tree, so I ran 'git reset --hard multiplayer' (36b3418d) before starting. My commit sits on top of the wave-1 merge.
2) The common checks (finite numbers, MaxText, MaxList) read each command's public properties by reflection, with the property lists cached per type, instead of hand-written checks per command. This also covers the records a command carries (InventoryItemToken, PlannedBuilding) and any field added later. The per-command rules are still an explicit table.
3) The refusal texts are public constants on CommandGate (for example NotYours = "Not your characters.", HostOnlyText = "Only the host can do that."). The spec does not give the texts.
4) Two choices where the spec is open: PlaceBuildings refuses the whole command if any one plan fails rather than filtering plans out, and a SelectCommand with an empty list is refused (filtering leaves nothing).
5) The host-only types also get refusing rules, so they appear in Ruled; HostOnly() is false for DebugCommands, which pass when Cheats is on.
I touched no files outside the package.

### Review remaining
- Not fixed, only noted: rules index non-nullable string or list fields directly (for example x.Research.Length, and Buildings.Find(site) or Machines.Find(site)). This depends on N2's CommandCodec never decoding null into a non-nullable field. D2 marks only PlannedBuilding.Parent and InspectBuildingCommand.Instance as nullable, so I left it as the codec's contract.
- The full suite (Slow and Bench) was not run, only the quick run.

## S4

### Summary
Package S4 is done and committed. Build is clean and quick tests pass (only the known MyGuiScreenTests failure). Files changed:

- WorldPublish.cs: Publish is split into PublishCore(heal) and ViewOf(session, number, talkable).
  - PublishCore builds the viewer-independent part: characters in slot order with no viewer details, plus the shared world parts, including PlayerFaction, Money and Relations/RelationsRevision.
  - The relations row is rebuilt only when (table, Changes, Player.Faction, table.PlayerFaction) changes. The revision goes up only when the values differ.
  - The core is reused while stateStamp is unchanged. stateStamp goes up in RunTick, in ApplyPausedInterfaceCommands when it applies commands, in PreparePopulation and in Present.
  - ViewOf adds the viewer's details on top: Selected, Inspected, Skills, Inventory, AllSkills, SkillXpMultipliers, Hud, HealthParts, ItemGrid, Bounties, Path, CarriesShackles and StealthKoChance.
  - It also fills Viewer, Trade, Conversation and DialogueRequest for that player, PlayerSquads(this, n) (only that player's squads, fixing what S1 left), ActiveSquad, InspectedBuilding, the open container in Machines, Prospected, InCreation and CreationRaces.
  - CharacterSnapshot.Owner = OwnerOfSlot.
  - Public PublishFor(int) reuses the core or builds one without heal floaters. It uses Player.Find, so it never creates a session; an unknown player gets an empty overlay. Talkable is filled only here: range 1500 on XZ, at most 64 nearest first, cached per player for 15 ticks while the talker stays the same. The tick's Snapshot leaves Talkable empty.
- World.cs: CanTalk on a replica reads Snapshot.Talkable. Adds IsReplica, MakeReplica(seed) (no systems and tick 0, else InvalidOperationException) and Present(previous, current).
  - Present sets both snapshots and Tick. It copies into PlayerState the faction, money and the local session's selection, inspected character, active squad, InCreation, CreationRaces and Prospected.
  - RunTick throws "a replica world never ticks" on a replica and bumps the stamp otherwise.
- World.Interface.cs: replica guard and stamp. JoinPlayer and TransferSquad are added to the paused filter, and the paused path calls PopulationSystem.ApplyJoins in the same Inputs phase.
- World.Loading.cs: PreparePopulation returns true on a replica and bumps the stamp.
- Meitou.Engine/WorldSession.cs: adds the SessionMode enum, Mode, SimulationAlpha/MotionAlpha (the presented values on a replica, Simulation.Alpha when local) and Present(previous, current, alpha, motionAlpha), clamped to 0..1 and 0..2.
  - ReplaceWorld(..., SessionMode mode): a replica must have no systems and its setup must call MakeReplica, and a local world must not be a replica; otherwise ArgumentException, and the new world is discarded. The alphas reset to 0.
  - Advance runs only control ticks in Replica mode; AdvanceSimulation throws there.
- GameHost.Characters.cs: positions and yaw use MotionAlpha, animation and ragdolls keep SimulationAlpha. On a replica, a looping clip that wrapped between two snapshots now runs on through its end instead of jumping.
- docs/simulation.md: the "Per-viewer snapshots" placeholder is replaced with the S4 contract (core and views, overlays, PublishFor and the stamp, relations, Talkable, paused path, replica worlds, WorldSession).
- tests/Meitou.Tests/Simulation/PublishForTests.cs: 5 tests, see the test notes.

### Deviations
1. World.Present does not throw when current.Tick is lower than Tick; Tick simply follows the snapshot. D3 §6 asked for current.Tick >= Tick, but D3 §5's jitter buffer can snap the presentation backwards, and throwing there would crash the client. The contract only says "Tick = current.Tick".
2. ReplaceWorld(mode: Local) also throws ArgumentException if setup made the world a replica, so Mode and IsReplica always agree. The contract only states the Replica-mode checks.
3. One file outside the S4 row changed: a one-line doc comment in src/Meitou.Simulation/WorldSnapshot.cs. PlayerSquads now says "the viewer's squads (PlayerSquads(World, int))". D1 §5 lists WorldSnapshot.cs under WP4, and no code there changed because S1 already declared the members.
4. The core fields, the Talkable cache and the relations cache live in WorldPublish.cs, which is part of the same partial World class. World.cs only increments the stamp in RunTick.
5. Talkable's "awake" test is read as "Medical is not Incapacitated" (neither unconscious nor dead). The talker is the first selected character that resolves and is a player character.
6. My worktree branch started at master (8f26eca7), not at multiplayer. While it was still clean I reset it to multiplayer (36b3418d, wave 1) before starting, so the commit sits on top of wave 1.

### Review remaining
- Not changed, judged within the contract: PublishFor for a player number with no session still lists the squads that number owns, because PublishSquads works from squad ownership, not from the session. The rest of the overlay is empty.
- Accepted deviation: World.Present lets Tick step backwards; D3 §6 asked for current.Tick >= Tick, and CONTRACT only says Tick = current.Tick.
- Minor, left as is: the per-tick ViewOf allocates a small SortedSet, and PublishCore copies the list into an array.

## S5

### Summary
S5 "Joining and squad transfer" is done, and the build and quick tests pass. Changes:
- src/Meitou.Simulation/PopulationSystem.Player.cs: the squad-building part of StartPlayer moved into BuildStart(world, start, key, at). StartPlayer keeps the same key, random rolls, placement loop and SpawnSquad order, and the GoldenHashTests pin did not move.
- New public AddPlayer(world, player, start, near) with const JoinRadius = 150. It throws if no game exists or if player <= 0. On a rejoin (the player's squads still have a living member) it makes no new squad: it runs KeepActive, makes a squad with a living member active, and selects its first living member when the selection is empty. Otherwise it uses key = Mix(PlayerStart ^ StableHash(start.Id) ^ Mix(player<<32 ^ Tick)) and places the squad at the first walkable of 20 points in the disc (ForceStartPos is ignored). It spawns the squad owned by the player, named with NewSquad's unique Nameless_n rule. The session gets Squad, Selection = [first], InCreation, CreationRaces and Focus = near. Faction, Money and Research are untouched and the faction head cap is not checked.
- ApplyJoins now has a body: for each JoinPlayer it picks the start (StartId if it is a NEW_GAME_STARTOFF, else the start named Wanderer, else the first) and calls AddPlayer. With no game, player 0, or no start at all, it posts a message to the issuer. It works from PopulationSystem.Inputs, which wave 1 already wired, and from the paused path S4 will wire.
- src/Meitou.Simulation/PopulationSystem.cs: SpawnSquad gains `int owner = 0`, and the Squad initializer sets Owner.
- src/Meitou.Simulation/SquadManagement.cs: new TransferSquad case (Transfer). It needs a player squad and a receiving player with a session. It sets Owner, removes the squad's members from every other session's selection, then runs KeepActive.
- docs/simulation.md: the "Joining" placeholder is replaced with the contract, including the engine choices (no money added for joiners, head cap skipped), rejoin idempotence, the JoinPlayer start lookup and TransferSquad.
- tests/Meitou.Tests/Simulation/PlayerJoinTests.cs: 3 tests, listed under testNotes.

### Deviations
1. The worktree was created from master (8f26eca7), not from multiplayer. Before doing any work I fast-forwarded the branch to multiplayer (36b3418d, wave 1). The sibling wave-2 worktree branches I checked (for example worktree-wf_f28ac482-87c-1) are also on 8f26eca7, so they may be missing wave 1 too.
2. PopulationSystem.cs Inputs: nothing to add. Wave 1 already calls ApplyJoins(world, commands) from Inputs. The only change in that file is the SpawnSquad owner parameter.
3. A new joiner squad's Index is "last in the player's order", which is NewSquad's rule, instead of D1's fixed 0. It is 0 in the normal case and avoids two equal Index values when the player still has squads with no living members.
4. TransferSquad also puts the squad last in the new owner's order and renumbers the old owner's squads. D1 lists only Owner, deselection and KeepActive; this keeps each owner's Index values unique. A transfer to the current owner, a non-player squad, or a player without a session does nothing.
5. On a rejoin, the session's active squad is also switched to one that has a living member, and the selection gets the first living member. D1 says "the first member of the active squad"; this avoids selecting a dead character.

### Review remaining
- Not fixed (edge case): player squads skip GateUniques, so a start whose squad list holds a `unique` CHARACTER would spawn a second copy for each joiner and rebind the unique to it. Base-game starts are not known to do this.
- Not fixed (outside the spec): TransferSquad leaves the old owner's per-session references to the moved characters in place (ContainerOpener, ContainerWalker, DoorWalker, Grant, Inspected). D1 lists only Owner, deselection and KeepActive.
- Join-while-paused is still untested. It is deferred to T1, which needs S4's paused filter. The paused path already wraps Inputs in BeginTick/EndTick, as R2 requires.
- The full Slow run was not done in this review.

## S6

### Summary
S6 "Save roster and owners" (D1 §7) is done, and the build and tests are green.

- New file src/Meitou.Simulation/Saving/RosterSave.cs holds the key constants and the read/write logic, in the same style as SlaveSave.
  - Write: removes any stale `meitou player*` keys from CAMERA. If some session has a GUID, it writes `meitou players` plus `meitou player<k> id` (GUID in "D" format), `meitou player<k> name` and `meitou player<k> number`, listing every session with a GUID in number order.
  - WriteOwner: sets the player platoon's `meitou owner`, or removes the key when there is no roster or the owner has no GUID.
  - Read: skips entries with a bad or repeated GUID or number. The local GUID becomes 0. The entry that held 0 takes the local player's old number, or one past the highest if the local player is not in the roster. It joins the other entries and sets Local.Guid, plus Local.Name when the local player has an entry.
  - OwnerOf: 0 when there is no roster or no key. A GUID that is not in the roster also gives 0 and adds a line to loaded.Notes.
- SaveCapture.cs: calls RosterSave.Write before WritePlayerSquads. WritePlayerSquads takes the owner map and calls WriteOwner for each platoon it writes (-1 for a squad that is not a player's). Single-player saves stay byte-identical: the only extra work is removing keys that do not exist.
- SaveLoader.cs:
  - SaveLoadOptions has a new `Guid LocalPlayer` (Empty means a single-player load: the roster is ignored and every squad goes to 0).
  - The roster is read before the platoons, and LoadPlayerPlatoon gets the owner.
  - Player 0's initial squad is the first platoon it owns. The CAMERA selection is filtered to characters whose squad owner is 0. The fallback for the shown squad uses PlayerSquads(world, 0).
  - SquadManagement.KeepActive(world) runs at the end.
- docs/formats/save.md: a new CAMERA table row and a new "Multiplayer roster and owners" subsection after "Player platoons (Meitou mapping)".
- Tests: tests/Meitou.Tests/Save/MultiplayerSaveTests.cs has the three approved cases, on the synthetic town with no game install:
  - owners round-trip through capture and load; loading as the host keeps the numbers, loading as the guest swaps them, and squads, active squads and selection follow;
  - a single-player save has no roster or owner keys;
  - a load with an empty LocalPlayer gives every squad to 0 and creates no extra sessions.

### Deviations
1. My worktree branch was created from master (8f26eca7), not from 'multiplayer'. The worktree was clean and master is an ancestor of 'multiplayer', so I fast-forwarded the branch to multiplayer (36b3418d) before starting. The commit sits directly on top of 36b3418d.
2. I added one file the contract row does not list: src/Meitou.Simulation/Saving/RosterSave.cs (new and owned by this package; it keeps the roster logic out of SaveCapture and SaveLoader).
3. D1 §9 says save keys go in docs/formats/save.md "with one line under 'Saves'", which may mean docs/simulation.md. That file is not in the S6 row, so I documented everything in save.md only and did not touch simulation.md.
4. `squad index` is still written as the position in the cross-owner PlayerSquads(world) order, as before, so single-player saves stay identical. Loading does not renumber per owner. Each owner's relative order is kept, and NewSquad uses the owner's last Index + 1, so gaps do no harm.
5. The selection filter on load uses the character's squad Owner (0, or no squad), not World.OwnerOf. OwnerOf returns -1 for non-IsPlayer members such as a prisoner kept in a player squad, which would have changed single-player loads.

### Review remaining
- The package adds src/Meitou.Simulation/Saving/RosterSave.cs, which is not in the S6 file list. It is a new public static class in the same style as the other *Save helpers, and no other package touches it, so I accepted it.
- D1 §9 asks for one line under 'Saves' in docs/simulation.md, but that file is not in the S6 file list. Everything is documented in save.md only, and a later docs package or the merge can add the cross-reference line.
- Checked with no defect found: the contract signature SaveLoadOptions.LocalPlayer; single-player saves stay byte-identical (keys are removed from OrderedDictionary only when present, and nothing is written when no session has a GUID); KeepActive and the owner-0 selection filter do nothing in single-player, so no hashed state moves; the remap rules (local becomes 0, the old holder of 0 takes the local player's number or max + 1); unknown owner GUIDs add a load note; the tests match the approved list (d) exactly.
- Full run with the game install not done (only the quick run). Quick run: 492 passed, 1 failed, and the failure is the known baseline MyGuiScreenTests.ListsScrollSelectAndClipAndTabsHideInactiveContents.

## T0

### Summary
Added the T0 tests and no product code. MultiplayerSessionTests has 3 new cases: squad limits and KeepActive per owner, StateHash staying unchanged for single player, and PayBail taking the issuer's payer (plus a no-payer-in-range refusal). PrimitivesTests has 8 NetReader failure cases covering every item you listed, including invalid UTF-8 (the reader checks it), each throwing NetFormatException. FcsRecordCodecTests has the duplicate-key refusal, checked for float, bool and reference-category keys with a distinct-keys control. No product bug found.

### Deviations
The worktree was created from master, not the multiplayer branch, so I ran `git reset --hard multiplayer` on it before any work (it had no commits of its own). The new tests are therefore based on multiplayer commit 36b3418d. The PayBail test builds its own small arrest town from ArrestTests.Town because JailTests.Jail is a private nested class. The contract's "invalid UTF-8 if the reader checks it" is covered because it does check.

### Review remaining


## N2

### Summary
N2 command codec: src/Meitou.Net/Commands/{CommandTags,CommandCodec,CommandFields,CommandBatchCodec}.cs, docs/net.md section, tests/Meitou.Tests/Net/CommandCodecTests.cs (4 tests). 81 tags incl. 241 JoinPlayer and 242 TransferSquad as host-only. Writers clamp strings (MaxName bytes), id lists, plans and non-finite floats. Batch write rolls back the writer and string table on failure.

### Deviations
1) The worktree started on master (8f26eca7), not 'multiplayer'; I fast-forwarded it to 'multiplayer' (36b3418d) before working. 2) Added internal CommandCodec.ReadUnchecked (visible to Meitou.Tests) so host-only layouts can be round-tripped in tests; public Read still refuses them. 3) Batch/result codecs write the body only, without the MessageType byte (caller adds it); documented in docs/net.md. 4) byte? / bool? / float? are encoded as a presence byte plus value (the spec only says 'byte?: byte'). 5) CommandBatchCodec.Write throws ArgumentException (and rolls back) for more than 64 commands or over 65536 bytes rather than clamping, since silently dropping commands would desync sequence numbers. No files outside the package were touched except docs/net.md (N2 placeholder replaced).

### Review remaining


## N3

### Summary
N3 Transport is done and committed. The package lives in src/Meitou.Net/Transport/ (namespace Meitou.Net.Transport):
- INetTransport.cs: NetConnectionId (ids start at 1 and are never reused within one transport), NetDropReason (Closed, Timeout, Rejected, Error), NetLinkStats, INetListener (Request, Connected, Received, Dropped), and INetTransport, INetServerTransport and INetClientTransport. These follow D3 §2, using NetChannel from Meitou.Net. Events arrive only inside Poll, on the calling thread, and nothing sent during a poll is delivered in that same poll. Each accepted peer and each Connect gets exactly one Dropped, including after a local Drop, but never after Dispose. Connect never throws for network failures; they end in Dropped instead.
- PeerSink.cs: PeerSink(INetTransport, NetConnectionId) : IPacketSink.
- LiteNetTransport.cs: one class serves both host and client, with a parameterless constructor. It uses one NetManager with ChannelsCount = 4, UnsyncedEvents = false, AutoRecycle, EnableStatistics, DisconnectTimeout = 15000 and IPv6Enabled. Snapshots uses DeliveryMethod.Sequenced and the other channels ReliableOrdered. Connect data (Magic + Version) goes raw through Connect(IPEndPoint, ReadOnlySpan<byte>) and comes back through ConnectionRequest.Data, with Accept() or Reject(bytes). MaxUnreliablePayload comes from GetMaxSinglePacketSize(Sequenced). Drop calls NetPeer.Disconnect(bytes), and the payload is read back from DisconnectInfo.AdditionalData. The transport keeps its own connection ids on peer.Tag, because LiteNetLib reuses its peer ids. Poll runs PollEvents and then TriggerUpdate. A port that cannot be bound throws SocketException. I checked every LiteNetLib member used against the 2.1.4 assembly (a reflection dump plus the XML docs, then a successful compile). The 2.1.4 XML docs say Start runs LiteNetLib's own logic thread, which supports R8; docs/net.md records this as Observed.
- LoopbackNetwork.cs: LoopbackOptions has D3's LatencyMs, JitterMs, UnreliableLoss, Mtu = 1200 and Seed, plus ReliableLoss and Duplicate. The network has one Server(), any number of Client()s, and Sever(clientSideId). It is deterministic: one seeded Random drawn in send order, delivery ordered by (due time, send order) from the clock. Reliable channels never lose and keep order per channel; each lost transmission adds a resend delay. Snapshots can lose, reorder and duplicate packets, and the receiver drops anything sent before the newest packet it has delivered. A Snapshots payload over Mtu throws.
- NetClock.cs: INetClock (NowMicroseconds), StopwatchNetClock and ManualNetClock (settable NowMicroseconds, Advance(TimeSpan)).
- docs/net.md: the "Transport" section replaces the N3 placeholder; no other section was touched.
- Tests: tests/Meitou.Tests/Net/LoopbackTransportTests.cs has 4 tests: accept and refuse with payloads plus a connect to an unlistened port timing out; complete per-channel order under 30% reliable and unreliable loss with jitter; the sequenced channel never delivering an older or duplicate packet; drops reaching both sides once, covering Drop with payload, Sever and Dispose. tests/Meitou.Tests/Net/LiteNetTransportTests.cs has one real-socket smoke test on 127.0.0.1 with an OS-assigned port: connect payload, Control, Snapshots, a 5000-byte fragmented Events packet, and a drop payload. It finishes in well under a second.

### Deviations
1) My worktree branch had been created from master (8f26eca7), not from 'multiplayer', so src/Meitou.Net was missing. It had no commits of its own, so I fast-forwarded it to multiplayer (36b3418d) before starting. My commit sits on top of the wave-1 merge.
2) I added `int Port { get; }` to INetServerTransport, which D3 §2 does not have. It gives the bound port after Listen, including the one the OS assigns for port 0. The real-socket test needs it, and NetHost can use it for logging.
3) LoopbackNetwork takes an INetClock instead of D3's ManualNetClock, so a ManualNetClock still works. LoopbackOptions adds ReliableLoss (a lost transmission becomes a resend delay; reliable data is never lost) and Duplicate, as the task asked for loss, reorder and duplicate simulation. D3's members are unchanged.
4) LiteNetLib settings differ from D3's 1.3-era text as CONTRACT §2b requires: Sequenced instead of Unreliable, and DisconnectTimeout 15000 instead of 10000.
5) Contract choices I made where D3 was silent: a local Drop also raises Dropped(Closed, empty) at the next poll; Send to a peer that is not connected is ignored; MaxUnreliablePayload, Address and Stats return 0, "" and default for an unknown peer; in loopback, a connect to a port nobody listens on times out one round trip later, and a close arrives after the reliable data sent before it.
6) The smoke test is in its own file, LiteNetTransportTests.cs, rather than in LoopbackTransportTests.cs. It is not marked [Slow] because it uses neither the game install nor long run time. Inside the sequenced-channel test I also assert MaxUnreliablePayload and that an over-MTU payload throws. Nothing outside src/Meitou.Net/Transport, these two test files and the docs/net.md Transport section was touched.

### Review remaining
- Packages that write Reject payloads (N2 codec, N11 host) must clamp the Reject reason so the encoded payload is at most INetTransport.MaxDropPayload (1000 bytes). Otherwise Drop or Request refusal throws ArgumentException. Worth relaying in the CONTRACT wave notes.
- Every reliable message must now stay within NetLimits.MaxReliableMessage (65536). Both transports throw above it, and LiteNetLib's MaxFragmentsCount of 65 rejects anything larger on receive.
- Verification: 'dotnet build -m:2' passed with 0 warnings. The quick run gave 494 passed and 1 failed, the known baseline MyGuiScreenTests.ListsScrollSelectAndClipAndTabsHideInactiveContents. All 5 transport tests pass. The full run with the game install was not done because nothing touches game data.

## N4

### Summary
N4 snapshot core wire is in src/Meitou.Net/Replication/Snapshot/ (namespace Meitou.Net.Replication). CharacterWire holds the 13 change-mask groups, with Owner in Identity as a zigzag varint. Its Quantize, ToSnapshot and DiffMask are static or instance members of that class. CharacterWireCodec.WriteGroups/ReadGroups write and read the groups a mask names onto a baseline. The caller (N10) writes the record header and the mask varint. Position is absolute without a baseline and a zigzag difference with one.

The supporting codecs:
- AnimationWire and AnimationLayerWire: a library index plus a name through the string table, a u16 phase of Time/Length, u16 weights with raw-float escapes, and delta flags against the baseline layer at the same index.
- RagdollWire: set bones only, smallest-three rotation, i16 positions at 1/256 (raw floats for the whole pose when any position reaches 127 units), u16 weight.
- ProjectileWire: Q64 position deltas, 16-bit direction components, a stretch float, a stuck bit, and the mesh only when it differs from the baseline's. Its Write/Read take a mask from DiffMask. The caller writes the id and back-reference.
- WireText: an internal helper that cuts text to its byte limit and replaces non-finite floats with a fallback.

Writer-side limits cut text to MaxName bytes, replace non-finite floats, and cut layers to 8, watchers to 32 and jobs to 64. They drop a ragdoll with more than 512 bones and any job whose TaskType is undefined. A layer whose definition index is not in the library is sent by name. docs/net.md "Snapshot core" replaces the N4 placeholder. tests/Meitou.Tests/Net/CharacterWireTests.cs has 4 facts:
- full and delta round trip, including a steady character costing 0 bytes
- projectile round trip plus hostile input cut to the limits
- Quantize∘ToSnapshot idempotence over 300 random snapshots
- precision: smallest-three at most 0.005 rad, ragdoll position at most sqrt(3)/512, phase, yaw, and positions at the world edge within 1/128

### Deviations
1. The branch started at master, not 'multiplayer'. I fast-forwarded it to 'multiplayer' (36b3418d) before starting, so the commit sits on the wave-1 merge.
2. Signatures differ from CONTRACT §2b because LookRegistry, ILookResolver and CharacterDetail belong to N5/N7 and do not exist in this worktree.
   - Quantize(CharacterSnapshot, Func<CharacterAppearance?, uint> lookIdOf, AnimationLibrary). The AnimationLibrary parameter is needed to validate definition indices at write time. N10 should pass registry.IdOf and its library.
   - ToSnapshot(CharacterId, Func<uint, CharacterAppearance?> resolveLook, AnimationLibrary). It has no CharacterDetail parameter and returns a snapshot with default detail fields, which the caller merges with 'with'.
3. In RagdollWire I use a private Decode(uint) instead of Quant.FromSmallestThree. It keeps the dropped component strictly the largest, with about a 1e-5 relative margin. Without this, near-equal top components made repacking pick a different index (a 2-cycle), so Quantize∘ToSnapshot was not idempotent (reproduced at roughly 0.2% of random bones). Quant is unchanged and the angle error is still at most 0.005.
4. Ragdoll wide-position switch: it triggers when any position rounds to 127*256 steps or more, not at 'abs >= 127'. Otherwise a value such as 126.998 would encode as 127.0 and switch mode on re-quantize.
5. Added public constants CharacterWire.MaxWatchers = 32 and MaxJobs = 64 instead of editing NetLimits (an N1 file).
6. Projectile body layout: the spec only listed the fields. I defined a flags byte with bits 0-3 for position, direction, stretch and mesh, and bit 4 as the stuck value. A direction settled by iteration keeps Quantize∘ToSnapshot idempotent.
7. Watchers are stored as absolute Q64 in the wire and encoded relative to the same wire's Position. The decoder reads the Position group first, so this is consistent.

### Review remaining
- Not a defect: watcher positions are decoded relative to the character's Position, so a caller (N10) must pass a mask from DiffMask, not an arbitrary one. A Stealth-only mask is only safe when Position is unchanged against the baseline. docs/net.md does not state this.
- Not a defect: ToSnapshot, Quantize and the CharacterWireCodec signatures differ from the CONTRACT text because LookRegistry, ILookResolver and CharacterDetail do not exist yet. N5, N7 and N10 must adapt, and the deviations are already documented.
- I did not run the Slow tests or the full quick suite. I made no changes, and the implementer reported 493 passes and only the known baseline failure.

## N5

### Summary
N5 done. ViewCodecs.World.cs covers kinds 5-11: buildings, ground items, containers, door picks, doors, unlocked, inspected. ViewCodecs.Production.cs covers kinds 12-14: machines, research, crafts. Each kind has an item codec and Sync/Decoder factories such as BuildingsSync() and BuildingsDecoder(). The non-list sources (doors, unlocked set, crafts) go through WorldMappedSync/WorldMappedDecoder. Kind 11 uses WorldSingleSync/WorldSingleDecoder. Research uses ResearchViewSync/ResearchViewDecoder. Writers clamp strings and lists and write non-finite floats as 0. Added the docs/net.md N5 section and tests/Meitou.Tests/Net/WorldViewCodecTests.cs (8 tests: every kind round-trips, plus keyed insert/update/remove/reorder).

### Deviations
1) The worktree started on master, without Meitou.Net, so I ran `git reset --hard multiplayer` (36b3418d) on my own empty branch before starting. 2) The spec does not fix an API for non-list views, so I defined public WorldMappedSync/WorldMappedDecoder, WorldSingleSync/WorldSingleDecoder, ResearchViewSync/ResearchViewDecoder, DoorEntry and CraftEntry in my two files. N10 must call them; N6 should avoid these names. 3) MachineSnapshot's storage grid and craft output need InventoryView codecs, so I wrote private ones (WriteStore*/ReadStore*). N6's kind-21 codec will duplicate these; they can be merged later. 4) The Doors, Unlocked and Crafts flatteners sort by key. Sorting keeps a rebuilt dictionary or set from looking reordered. 5) I added the min-interval constants as public ViewCodecs fields. 6) No other files were touched outside the package, apart from docs/net.md, as the contract requires.

### Review remaining
- Not defects, for later packages: a single machine item with a very large storage grid could exceed MaxReliableMessage (it is always sent whole). WorldMappedSync does not clear its cached flattened list on Reset, which is harmless because the inner sync's Reset forces a resend. The private WriteStore* codecs duplicate what N6 will write for kind 21.

## N6

### Summary
N6 done. Added src/Meitou.Net/Replication/Views/ViewCodecs.Player.cs and ViewCodecs.Detail.cs (partial static class ViewCodecs). ItemCodec<T> properties for Header (HeaderView), Towns, Camps, Squads, Trade, DialogueRequest, Speech, Relations (RelationsView), Talkable, Prospected, plus Stats/Health/Inventory/Path detail records (StatsDetail etc., keyed by CharacterId) with StatsOf/HealthOf/InventoryOf/PathOf extractors and ApplyTo helpers. KeyedViewSync factories: CampSync, SquadSync, SpeechSync, StatsSync, HealthSync, InventorySync, PathSync (decode with KeyedViewDecoder). Conversation (16) is stateful: ConversationViewWriter/ConversationViewReader send the log as kept + new lines. Writer-side clamps (interned strings cut to MaxName, text to MaxString, lists to limits, non-finite floats to 0). PlayerCovered/DetailCovered arrays for the coverage test. docs/net.md N6 section written. tests/Meitou.Tests/Net/PlayerViewCodecTests.cs has one round-trip test per kind (16 tests).

### Deviations
1) The worktree was created at master, not the multiplayer branch; I fast-forwarded the worktree branch to 'multiplayer' (36b3418d) before starting. 2) Single views (Header, Towns, Trade, Relations, Talkable, Prospected...) have only ItemCodec bodies and no sync class: change detection (reference or bytes) is left to N10. 3) The conversation view uses a dedicated stateful writer/reader pair instead of an ItemCodec because of the kept-log delta. 4) Detail views use Q64 for path waypoints (spec said raw/UI values; quantized is smaller and consistent with positions). 5) Detail records are keyed by CharacterId, which the spec left open. 6) Internal helper class PlayerWire lives in ViewCodecs.Player.cs; the rest of ViewCodecs members are uniquely named to avoid clashes with N5's partials (possible clash only if N5 also declares PlayerWire/StatsOf etc., unlikely). 7) Trade lists are not split to fit MaxViewBody; only MaxListItems is applied (documented as Unknown).

### Review remaining
- Trade views are not split to fit MaxViewBody (documented as Unknown in docs/net.md); the single-view change detection is left to N10 as the implementer noted.

## N7

### Summary
N7 Looks is done and committed. All the new code is in src/Meitou.Net/Replication/Looks/ (namespace Meitou.Net.Replication):
- AppearanceRecipe.cs: an abstract record AppearanceRecipe with two nested sealed records. `Rolled(RecordId, FactionId, Seed)` and `Kit(RecordId, LoadoutWire)` cover the two Create calls. There is also an optional `Shape` (CharacterShapeInput), a `Kind` (RecipeKind enum: Rolled = 1, Kit = 2), `FromLook` (an animal gives its species recipe, otherwise a Kit from look.Loadout, otherwise null) and `Build(source, db)`. LoadoutWire, LoadoutItemWire, LoadoutWeaponWire and LoadoutLimbWire hold a Loadout with every record as a string id, plus the full CHARACTER_APPEARANCE FcsRecord. The lists are copied when the look is made and Notes are dropped. `ToLoadout(db)` returns null if any record it names is missing.
- RecordingAppearances.cs: forwards all four IAppearanceSource members, including the ones with default bodies. It stores each Create's recipe in a ConditionalWeakTable keyed by the returned look. An animal look is always recorded as Rolled(speciesId, null, 0) because GeneratedAppearances shares one instance per species. `RecipeOf(look)` and `Inner` are public.
- LookRegistry.cs: `IdOf` returns 0 for null. Otherwise each look object gets one id on first use, and ids are never reused. The recipe is taken at that point (the recorded one, else FromLook, plus look.Shape if set). `RecipeOf(id)` returns null for an unknown id or a look that has been collected. A sweep drops collected looks once the count doubles. It is for the frame thread only.
- LookCodec.cs: `WriteDefinition` / `ReadDefinition` handle the whole message: 0x31, then the id varint (0 is refused), the kind byte, the body and a shape-presence flag. `Write` / `Read` handle the recipe on its own. Ids go through the channel's string table and the appearance record goes through FcsRecordCodec. On the write side, ids longer than MaxName are refused, list counts are checked, and non-finite shape floats are written as 0, so a reader never refuses what a writer produced.
- LookCache.cs: declares `ILookResolver` (as in the contract) and implements `LookCache(IAppearanceSource, GameDatabase, bool background = true)`. `Define` builds the look on the thread pool, or synchronously when background is false. A repeated id keeps its first recipe. `Resolve` returns null until the build is done, and a build that throws, or a recipe naming a missing record, gives a null look (marker). Extra helpers: `IsReady`, `RecipeOf`, `WhenBuilt`, `Count`.
Docs: in docs/net.md I replaced the "Pending (package N7)" line under "## Looks" with the contract, the field order and Verified/Observed marks. Tests: tests/Meitou.Tests/Net/LookTests.cs.

### Deviations
1. The worktree started on master (8f26eca7), not on multiplayer, and src/Meitou.Net did not exist. It was clean and the branch is this package's own, so I ran `git reset --hard multiplayer` (36b3418d) before doing any work.
2. Additions beyond the contract signatures, all inside my files: the RecipeKind enum; public nested records AppearanceRecipe.Rolled and AppearanceRecipe.Kit (D2's shape, made public); an optional AppearanceRecipe.Shape so the "shape" in the task travels with a recipe; the LoadoutWire family; a static LookCodec class (no codec class was named for LookDefinition); LookCache gets an optional third constructor parameter `background = true` (synchronous fallback for a source that is not thread-safe) and the extra members IsReady, RecipeOf, WhenBuilt and Count; RecordingAppearances.Inner; LookRegistry.Count.
3. Message framing: WriteDefinition writes the 0x31 type byte and ReadDefinition reads and checks it, so the receiver dispatches by peeking at packet[0]. ReadDefinition does not call ExpectEnd; the caller does, since there is one message per packet. N10 should follow this.
4. LookRegistry does not track which ids each client has been sent. Per CONTRACT §2b, the replicator keeps that "sent-look set".
5. A repeated Define with the same id is ignored rather than refused (ids are never reused by the host).
6. Missing optional records (material, manufacturer, model, prosthetic) also give a null look, not only missing required ones, following D2's "a record the client does not have gives a null look".
7. The [Slow] test also checks the FromLook fallback and animal looks within the same test (this is R5's verification). I added no other tests.

### Review remaining
- Not part of N7, from N1's FcsRecordCodec, which is already merged: its writer does not check the MaxName length of map keys or string ids, and does not check finiteness for float, vector or quaternion values the reader reads with ReadFinite. A Kit look with such an appearance record could be refused by the client. It should be fixed in FcsRecordCodec, not here.
- LookCodec does not check that a whole LookDefinition fits within MaxReliableMessage. Realistic Kit recipes are about 450 B, so only a pathological appearance record would hit this.
- N10 must catch the codec's ArgumentException for unencodable host data, such as an over-long mod id, or it will propagate out of the replicator's send.

## N8

### Summary
N8 events codec: src/Meitou.Net/Events/{NetEvent,EventCodec,EventFilter}.cs, tests/Meitou.Tests/Net/EventCodecTests.cs (8 tests), docs/net.md Events section. Kinds 1-5, 7, 8 round-trip; kind 6 refused. Writer clamps texts (on a character boundary), non-finite floats become 0, halves saturate, batch cut to 1024 events. EventFilter uses straight-line distance to Eye or the interest set for kinds 1-3.

### Deviations
1) The worktree branch started at master, not multiplayer, so I fast-forwarded it to 'multiplayer' (git merge --ff-only) before working. 2) EventCodec.Write/Read include the 0x40 message type byte (Read checks it), following the "0x30 | Epoch ..." layout; a dispatcher must peek the type byte rather than consume it. 3) StealthKoEntry lives in Meitou.Simulation.Carrying, so the files add that using. 4) Blood Colour is 3 bytes (0xRRGGBB), as the field's doc says. 5) Added the Epoch u16 to EventBatch per C7/CONTRACT.

### Review remaining
- Batch byte size is not capped on write; the N11 host must split batches if it enforces MaxReliableMessage
- Writer does not validate enum values (an undefined NoticeKind would be refused by the reader); sim enums are always valid

## N9

### Summary
N9 done. Added src/Meitou.Net/Session/{ProtocolCodec,Handshake,DataSetIdentity,PlayerColourRule}.cs: records and codecs for every protocol message (Write puts the MessageType byte first, ReadX reads the rest and calls ExpectEnd; ReadType helper; NetRules/SessionRules overloads are body-only), Handshake (Proof, CheckRequest, CheckHello, SanitizeName, AnimationHash), DataSetIdentity (Compute, Difference, Combined) with DataSetEntry and NetIdentity, and PlayerColourRule.Assign. Tests ProtocolCodecTests (4) and HandshakeTests (14 cases) added. docs/net.md "Protocol and handshake" section written.

### Deviations
1. The worktree was branched from master, not 'multiplayer', so I ran 'git merge --ff-only multiplayer' first (pure fast-forward to 36b3418d; no merge commit).
2. CheckHello takes the extra hostAnimationHash argument, as in the CONTRACT signature. The animation mismatch maps to RejectCode.Data with its own reason text, which D3 does not specify.
3. CheckRequest's 'connected' means remote players already in the game, excluding the requester and the host. The game is Full when connected + 1 >= maxPlayers. N11 must call it this way. This is documented in the XML comment and in docs/net.md.
4. Texts the spec leaves open: a bad connect magic gives Protocol with 'Not a Meitou connection. Update Meitou.'; a Hello whose id equals the host's gives Duplicate with 'Your player id is already in this game.'. CheckHello also re-checks Hello.Protocol first.
5. Writers clamp free texts to the reader limit (character-boundary truncation) and throw ArgumentException for a wrong-size fixed field (nonce not 16 bytes, proof not 0 or 32 bytes, over 1024 data-set files). The roster is cut to 256 entries and weather regions to 256. This follows the contract's wave-1 'validate or clamp on write' rule.
6. DataSetIdentity.Compute lists an unreadable file as hash 0 and length -1 instead of throwing.
7. Not done: the [Slow] check that two AnimationLibrary builds from the game install give the same AnimationHash. docs/net.md marks that as Unknown. The contract lists only the two test files, so I added no more.

### Review remaining
- Writers do not reject non-finite doubles or floats in clock and weather fields, although the readers refuse them. A NaN from the sim would make the peer drop the client. This is minor and left as is. It is a spec choice between throwing on write and dropping on read.
- The [Slow] check that two AnimationLibrary builds from the game install give the same AnimationHash is not written. docs/net.md marks it Unknown, and the contract lists no such test.

## U1

### Summary
U1 is done and committed (9e36b960) on worktree-wf_f28ac482-87c-15. Build has 0 warnings and 0 errors. The quick test run has one failure, the known baseline MyGuiScreenTests.ListsScrollSelectAndClipAndTabsHideInactiveContents. GoldenHashTests pass, no pins moved.

Important setup step: this worktree was created from master (8f26eca7), not from 'multiplayer'. It was clean, so I fast-forwarded it to multiplayer (36b3418d, the base the other wave-2 worktrees use) before doing any work.

New files:
- src/Meitou.Game/Multiplayer/NetUiContract.cs: the CONTRACT §2b "Game ↔ UI" types (NetRole, NetPhase, MultiplayerEventKind/MultiplayerEvent, NetQuality, IMultiplayerView built on Meitou.Net's RosterEntry/NetRules/NetDisconnect, HostSettings, the MultiplayerRequest records, MultiplayerDefaults taken from NetProtocol).
- src/Meitou.Game/Multiplayer/JoinAddress.cs: TryParse accepts host, host:port, [v6], [v6]:port and bare IPv6; port 1-65535 in digits, else the default.
- src/Meitou.Game/Multiplayer/PlayerColours.cs: the 8-colour palette with Of, MyGui, Selected, Absent and Assign.
- src/Meitou.Game/PlayerInterface.Net.cs: Net, MultiplayerRequested, Config, PlayerListHeld, RefreshMenuPause, Mine/OtherPlayers, PlayerInfo, Request, LogMessage, EnableButtons, EndText, PollNet and the screen dispatch.
- Stub partials for wave 3: PlayerInterface.{Multiplayer,Chat,Players,NetStatus,NetMenu}.cs, signatures using the contract types. ShowDisconnected is (NetDisconnect, bool reachedPlay).

Hooks in existing files:
- Ui.cs: registers the net screens; the roster line uses Mine; BindNetHud, chat history, status and the held player list are added; ShowUiPanel accepts "multiplayer".
- Faithful.cs: chat keys, net screen action/bind/content dispatch; JobDragEnd and RemoveJob use Mine.
- Frontend.cs: one-button alerts (Confirmation.Alert, Alert(), CancelButton); OpenPendingAlert in ShowTitle; CloseFrontend also closes the net screens; NetMenuAction; ImportGameButton is captioned MULTIPLAYER and opens the window; BeginHostedNewGame; BindNetMenu; DescribeSlot extracted from ReadSlotDetails.
- Hud.cs: PollNet; Notice goes through LogMessage; the C28 speed-button pre-check.
- PlayerInterface.cs, Picking.cs, Portraits.cs, Squads.cs: own-character filter. Treating, carrying, the context menu and LivingPlayerCharacters keep IsPlayer (commented).
- GameHost.Interactive.cs: player.Config = config; PlayerListHeld set in the control tick; F10 refuses to open with "Cheats are off in this game." when a session's Rules.Cheats is off.
- MyGuiScreenHost.Keyboard.cs: FocusBox(id, widget); NewBuffer now takes properties.
- InputBindings.cs: OpenChat = Enter, PlayerList = P. SettingsMenu.Descriptions.cs has their hints.
- UserConfig.cs: the §2b keys, EnsurePlayerId, LoadOrCreate(file), RecentGames with a top-level RecentGame class, plus a RememberGame helper.

Docs:
- docs/meitou-ui.md: new "## Multiplayer" section with a "Plumbing and ownership" subsection, plus heading-and-placeholder subsections for U2, U3, U4 and U5.
- docs/game/ui-input.md: "Multiplayer keys" and the F10 rule.
- docs/faithful-ui-parity.md: the deliberate multiplayer deviations.
- docs/game/ui-screens.md: PausedPanel/lbPaused, Verified against the installed Kenshi_MainPanel.layout. I also checked the ImportGameButton column against the installed Kenshi_MainMenu.layout.

### Deviations
1. The task text says "public Focus(screen, widget)". The method is named FocusBox(id, widget), as D5 E1 specifies, because MyGuiScreenHost already has a Focus property and C# does not allow a method with the same name.
2. The input action is named OpenChat (D5's name, which U3 will use) rather than "Chat".
3. No NetTimeButton stub. C8 deletes RequestPause/RequestSpeed and C28 gives the speed pre-check to U1, so the check sits directly in PressButton: "Only the host can change the game speed." when !Net.CanControlTime(LocalPlayer). U5's BindNetHud stub is kept for disabling the buttons.
4. Following the contract over D5: the UI uses MultiplayerEvent, RosterEntry, Meitou.Net NetRules and NetDisconnect. There is no NetEnd, NetPlayer, Progress or Downloading phase. EndText(NetDisconnect, reachedPlay) picks the title (DISCONNECTED once play was reached, else CONNECTION FAILED); the text is the Reason, with a fallback when it is empty. MultiplayerDefaults.Port is 40120.
5. Meitou title mode: instead of simply removing Hidden.Add("ImportGameButton"), the button stays hidden until MenuLayout gives it a position (U2 adds it to MenuButtons). Before that it would sit at its raw layout position in the Meitou menu. In Faithful mode it is enabled and captioned MULTIPLAYER now; until U2 fills OpenMultiplayer, clicking it does nothing.
6. Additions beyond the contract:
   - UserConfig.RememberGame(address, host, utc) and MaxRecentGames = 8, so U2 and G3 share one newest-first, unique, at-most-8 rule.
   - RecentGame is a top-level internal class, not nested in UserConfig.
   - An OtherPlayers(CharacterId) overload for the Alt-loot check.
   - PlayerColours.Assign uses the same rule as N9's PlayerColourRule (preferred if free, else lowest free, else -1). It duplicates that rule because N9's type does not exist in this worktree; a later wave can make it forward.
7. F10 gate: per D5 it applies to anyone in a session (host included) when Rules.Cheats is off, and only blocks opening; an already open menu can still be closed.
8. docs/faithful-ui-parity.md already lists the Esc menu multiplayer rows and the multiplayer title as contract (D5 E7 assigns that doc to WP-E). The Esc rows themselves arrive with U5.

### Review remaining
- Known baseline failure MyGuiScreenTests.ListsScrollSelectAndClipAndTabsHideInactiveContents is still present. It was there before this package.
- Accepted, not a defect: keyboard Space and F2-F4 clock requests have no local pre-check because they go through WorldSession, which is not a U1 file. The host refuses them with the same text, and docs/meitou-ui.md says so.
- Accepted, not a defect: PlayerColours.Assign duplicates N9's PlayerColourRule rule because N9's type is not in this base yet. A later wave can make one forward to the other.
- Accepted, not a defect: faithful-ui-parity.md and meitou-ui.md already describe the Esc rows and the MULTIPLAYER window as contract (D5 E7). OpenMultiplayer and the Esc rows are stubs until U2 and U5. In Faithful mode the title's MULTIPLAYER button is enabled and does nothing until U2.
- Accepted, not a defect: values read from meitou.user.json are not normalised (RecentGames null or longer than 8, HostMaxPlayers outside 2-8). This matches how the file's other keys are handled, so use sites (U2, G2, G3) clamp.

## G5

### Summary
SettingsMenu gets `HostRules` (Func<SessionRules?>?) and `Entry.Locked`. Attack slots, late dodges and blocks, squads keep their people, fights far away, healing far away, wild squads stay, doors close behind, raids as written and luckyren Mode read `Host?.X ?? config.X`. When HostRules returns non-null, those rows are marked Locked and " Set by the host." is added to the hint. A locked row is drawn dimmed with the host's value as static text and registers no Hit, so it can't be clicked, dragged or edited. Reset() leaves the rule keys alone on a client. In single player and on the host, behaviour is unchanged.

### Deviations
The worktree started on master, not the multiplayer branch, so I fast-forwarded it to `multiplayer` (it was an ancestor and the tree was clean) to get Meitou.Net and SessionRules. docs/net.md has no G5 placeholder and the contract names no doc for this package, so I wrote no docs. I added no tests, as instructed.

### Review remaining

