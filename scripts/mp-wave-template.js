export const meta = {
  name: 'mp-wave2',
  description: 'Multiplayer wave 2: 16 packages (dialogue/trade sessions, gate, per-viewer publish, joining, saves, codecs, transport, looks, protocol, UI plumbing) implemented in worktrees then reviewed',
  phases: [
    { title: 'Implement', detail: 'one agent per package in its own git worktree' },
    { title: 'Review', detail: 'adversarial review and fix in the same worktree' },
  ],
}

const SP = 'D:\\TEMP\\claude\\D--Github-MeitouClient\\c161db95-e19c-4784-9759-b1467629f936\\scratchpad'
const SPEC = `${SP}\\spec`
const SDK = `export DOTNET_ROOT='C:\\Users\\baz\\AppData\\Local\\Microsoft\\dotnet'; export PATH="/c/Users/baz/AppData/Local/Microsoft/dotnet:$PATH"`

const COMMON = `
You are implementing one work package of host-authoritative co-op multiplayer for MeitouClient (C#/.NET 10 Kenshi engine reimplementation).
You are running inside your OWN git worktree of D:\\Github\\MeitouClient, branched from the 'multiplayer' branch (wave 1 merged: player sessions/owners/issuer,
the Meitou.Net project with primitives, the engine clock seam). First run 'git rev-parse --show-toplevel' and 'git branch --show-current' and work ONLY inside
that worktree. Read ${SPEC}\\CONTRACT.md first (authoritative; its §7 "Notes from wave 1" is binding), then the detail spec sections it names for your package
(${SPEC}\\D1-sim.md, D2-wire.md, D3-session.md, D4-game.md, D5-ui.md). Read the code already merged (src/Meitou.Net, src/Meitou.Simulation/PlayerSession.cs etc.)
rather than assuming. 15 other engineers are working on the other wave-2 packages in parallel in their own worktrees; stay inside your package's files so the
merge is clean. Other packages' types you depend on may not exist yet in your worktree: only depend on what wave 1 merged or what your package creates.
Rules:
- Toolchain: in every Bash command prefix: ${SDK}   (the system dotnet is too old for global.json; never edit global.json).
- Build with 'dotnet build -m:2' from the worktree root (many builds run at once: keep -m:2); warnings are errors. Quick tests:
  'dotnet test --no-build --filter "Category!=Slow&Category!=Bench"'. Known baseline failure, ignore: MyGuiScreenTests.ListsScrollSelectAndClipAndTabsHideInactiveContents.
  Every other quick test must pass, and the GoldenHashTests pins must NOT change.
- Touch only your package's files (CONTRACT §4 row + files you create). If something outside is unavoidable for the build, keep it minimal and report it.
- Tests: the user approved every test the contract marks "Approved" or "Ask" for your package; add exactly those (and the cases the task below lists), no others.
  Tests that need the game install are [Slow] and use Assert.SkipWhen(GameInstall.Locate() is null, ...) / the InstallData helpers.
- Match surrounding style: dense /// doc comments citing docs, same idioms. Docs: write your package's subsection/placeholder in the doc the contract names,
  concisely, as current contracts (no narration); replace the placeholder line wave 1 left for you (docs/simulation.md "Multiplayer" subsections, docs/net.md
  "Pending (package Nx)" lines) without touching the neighbouring subsections.
- When done and green: 'git add -A' and commit on your worktree branch: one descriptive line in the repo style ("Multiplayer: ..."), blank line, a few detail
  lines, blank line, 'Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>'.
Return the structured result: branch, absolute worktree path, commit hash, summary, deviations from the contract (with reasons), build/test status.
`

const RESULT = { type: 'object', properties: { branch: { type: 'string' }, worktree: { type: 'string' }, commit: { type: 'string' }, summary: { type: 'string' },
  deviations: { type: 'string' }, buildOk: { type: 'boolean' }, testsOk: { type: 'boolean' }, testNotes: { type: 'string' } },
  required: ['branch', 'worktree', 'commit', 'summary', 'deviations', 'buildOk', 'testsOk', 'testNotes'] }
const REVIEW = { type: 'object', properties: { commit: { type: 'string' }, verdict: { type: 'string', enum: ['clean', 'fixed', 'blocked'] },
  issuesFound: { type: 'array', items: { type: 'string' } }, issuesFixed: { type: 'array', items: { type: 'string' } },
  remaining: { type: 'array', items: { type: 'string' } }, buildOk: { type: 'boolean' }, testsOk: { type: 'boolean' } },
  required: ['commit', 'verdict', 'issuesFound', 'issuesFixed', 'remaining', 'buildOk', 'testsOk'] }

const PACKAGES = [
  { id: 'S2', tier: 'strong', task: `S2 "Dialogue and trade per player" (CONTRACT §4 wave 2; D1 §3). DialogueSystem per-player PlayerTalk (SortedDictionary by player), Conversation.Owner,
TalkOrder/DialogueChoice/EndConversation per issuer, second player on a held NPC refused with "{name} is busy with someone else.", Effects (Trade defers OpenTrade with
Player = owner; DA_CHARACTER_EDITOR and Recruit grant per owner session; Recruit joins the talking player's active squad via NewSquad(owner)), OneAtATime over all talks,
PublishConversation(int)/PublishRequest(int), INpcHolder registration, the conditional hash layout of D1 §3 (player-0 talk hashed exactly where it was). TradeSystem per-player
Deal (SortedDictionary), OpenTrade checks HolderOf, Buy/Sell/ArrangeStock/CloseTrade by issuer, posts to the deal's player, PublishTrade(int), INpcHolder. docs/simulation.md S2 subsection.
Tests: tests/Meitou.Tests/Simulation/MultiplayerWindowsTests.cs: two players talk and trade at once with different NPCs; the second to the same NPC is refused and the first is
unaffected; the recruit joins the talking player's active squad; DA_CHARACTER_EDITOR grants only that player; containers are per session. Existing DialogueTests, TradeTests,
SurgeryTests and goldens must pass unchanged.` },
  { id: 'S3', tier: 'strong', task: `S3 "Command gate" (D1 §4). Create src/Meitou.Simulation/CommandGate.cs: GateOptions, GateResult, CommandGate.Admit/Ruled/HostOnly with a rule for EVERY
concrete SimCommand type (enumerate them all, including JoinPlayer and TransferSquad as host-only), refuse-by-default, the common checks and the per-command table of D1 §4,
restamping Player and Tick, stripping AttackOrder.Defensive and unmatched EditCharacter.Paid (peek the grant only). Read the S1 code (PlayerSession, World.OwnerOf, Actors...)
for the exact APIs. docs/simulation.md S3 subsection. Tests: tests/Meitou.Tests/Simulation/CommandGateTests.cs: every concrete SimCommand type (reflection over the assembly)
is in Ruled; foreign ids filtered or refused; host-only types refused; Defensive and Paid stripped (Paid kept with a matching grant); NaN refused; unknown players refused.` },
  { id: 'S4', tier: 'strong', task: `S4 "Per-viewer publish and replica world" (D1 §5, D3 §6, CONTRACT §2b [S4] and the replica rules under it). WorldPublish split into PublishCore/ViewOf with
the state stamp, World.PublishFor(int) (no PublishHeal, never creates sessions, no hashed changes), PublishSquads per viewer (S1 left it listing every owner), CharacterSnapshot.Owner,
WorldSnapshot Viewer/PlayerFaction/Money/Relations(+Revision)/Talkable (only from PublishFor; TalkableRange 1500, max 64, cached 15 ticks)/Prospected/InCreation/CreationRaces,
World.IsReplica/MakeReplica/Present (mirrors the viewer facts into the replica PlayerState; replica RunTick and ApplyPausedInterfaceCommands throw; replica CanTalk reads
Snapshot.Talkable; PreparePopulation returns true), World.Interface.cs (stamp, replica guard, JoinPlayer/TransferSquad added to the paused filter and the
PopulationSystem.ApplyJoins call), World.Loading.cs, Meitou.Engine/WorldSession.cs (SessionMode, Mode, Present(previous,current,alpha,motionAlpha), SimulationAlpha/MotionAlpha,
ReplaceWorld(..., SessionMode mode) with Advance running control ticks only in Replica mode), GameHost.Characters.cs (use MotionAlpha; looping-clip time unwrap when
interpolating animation layers between snapshots far apart). docs/simulation.md S4 subsection. Tests: tests/Meitou.Tests/Simulation/PublishForTests.cs: player 1's overlay holds
only their selection; Owner right on characters; PublishFor(0) equals Snapshot except Talkable; heal floaters and StateHash unchanged by PublishFor; replica guards throw.` },
  { id: 'S5', tier: 'strong', task: `S5 "Joining and squad transfer" (D1 §6). PopulationSystem.Player.cs: BuildStart refactor (StartPlayer keeps exactly the same keys, RNG calls and SpawnSquad order:
goldens must not move), SpawnSquad owner parameter, AddPlayer(world, player, start, near) with the rejoin path, ApplyJoins body (JoinPlayer handling: start lookup, AddPlayer;
callable from Inputs and from the paused path S4 wires), PopulationSystem.cs Inputs case, SquadManagement.cs TransferSquad case. docs/simulation.md S5 "Joining" subsection,
including the engine choices (joiners add no money, skip the faction head cap). Tests: tests/Meitou.Tests/Simulation/PlayerJoinTests.cs: AddPlayer leaves Faction, Money and
Research alone; the squad is owned by the joiner; that session is in creation; rejoin makes no new squad and selects the existing one; TransferSquad moves ownership and
clears other sessions' selections. (Join-while-paused is tested later by T1.)` },
  { id: 'S6', tier: 'strong', task: `S6 "Save roster and owners" (D1 §7). SaveCapture writes the CAMERA roster and PLATOON 'meitou owner' only when some session has a GUID (single-player saves byte-identical),
SaveLoader reads them with SaveLoadOptions.LocalPlayer (Guid) and the remap rules; KeepActive at the end. docs/formats/save.md keys. Tests: tests/Meitou.Tests/Save/MultiplayerSaveTests.cs:
owners round-trip through capture and load with the roster remap; a single-player save has no new keys; a load with an empty LocalPlayer gives every squad to 0. Existing
QuickSaveTests, SaveWorldTests, JailSaveTests, SaveWriteTests must pass.` },
  { id: 'T0', tier: 'fast', task: `T0 "Wave-1 extra tests" (user-approved Ask items from wave 1). Add ONLY tests, no product code changes: (1) in tests/Meitou.Tests/Simulation/MultiplayerSessionTests.cs add cases:
per-owner squad limits and KeepActive act per owner; StateHash changes only once a second session or a non-zero Squad.Owner exists (single-player hash unchanged); PayBail picks the
issuer's payer. (2) in tests/Meitou.Tests/Net/PrimitivesTests.cs add NetReader failure cases (truncated varint, over-long varint, count over max, string over maxBytes,
invalid UTF-8 if the reader checks it, enum out of range, ExpectEnd with trailing bytes, ReadFinite on NaN/Inf) each throwing NetFormatException. (3) in
tests/Meitou.Tests/Net/FcsRecordCodecTests.cs add: a duplicate key on the wire is refused. If a test reveals a real product bug, do not fix product code: report it.` },
  { id: 'N2', tier: 'fast', task: `N2 "Command codec" (D2 §4; CONTRACT §2b Commands). src/Meitou.Net/Commands/*: CommandTags (explicit stable ushort tags for every concrete SimCommand incl. 241 JoinPlayer (H),
242 TransferSquad (H); CommandOrigin), CommandCodec.Write/Read (all constructor fields AND init-only properties; Read sets Tick and Player; HostOnly refused always, Debug refused unless
allowDebug -> NetFormatException), CommandBatch/CommandResult + CommandBatchCodec, nested InventoryItemToken/PlannedBuilding, EditCharacter.Appearance via N1's FcsRecordCodec.
Writer-side limits. docs/net.md N2 section. Tests: tests/Meitou.Tests/Net/CommandCodecTests.cs: reflection completeness (every concrete SimCommand has a tag), round-trip of every
type with every property non-default, HostOnly and Debug refusal, batch round-trip.` },
  { id: 'N3', tier: 'strong', task: `N3 "Transport" (D3 §2 as amended by CONTRACT §2b Transport). src/Meitou.Net/Transport/*: the INetTransport family (server/client, connection ids, poll-based events on the
calling thread, channels per NetChannel), PeerSink(INetTransport, NetConnectionId): IPacketSink, LiteNetTransport over LiteNetLib 2.1.4 (NetManager ChannelsCount 4, Snapshots =
DeliveryMethod.Sequenced, others ReliableOrdered, DisconnectTimeout 15000, connect payload Magic+Version, MaxUnreliablePayload from the peer's MTU; confirm every LiteNetLib member
against the 2.1.4 assembly in the NuGet cache, e.g. by inspecting its XML docs/metadata), LoopbackNetwork + LoopbackOptions (deterministic, latency/loss/reorder/duplicate simulation
driven by a clock), INetClock, StopwatchNetClock, ManualNetClock. docs/net.md N3 section. Tests: tests/Meitou.Tests/Net/LoopbackTransportTests.cs (connect/accept/reject, ordered
reliable delivery under loss, sequenced drops older, disconnect events) and one real-socket smoke test of LiteNetTransport on 127.0.0.1 with an OS-assigned port (keep it quick).` },
  { id: 'N4', tier: 'fast', task: `N4 "Snapshot core wire" (D2 §5; CONTRACT §2b Replication). src/Meitou.Net/Replication/Snapshot/*: CharacterWire (the 13 change-mask groups, Owner in group 9 zigzag), quantization
(positions 1/64, Yaw16, animation time as 16-bit fraction of Length, weights), animation layer codec (AnimationLibrary index + name via string table), ragdoll BonePose codec (Set bones
only, smallest-three, root position, weight), projectile codec. Writer-side limits. docs/net.md N4 section. Tests: tests/Meitou.Tests/Net/CharacterWireTests.cs: round-trip,
Quantize∘ToSnapshot idempotent, quantization precision (smallest-three ≤ 0.005 rad per CONTRACT §7).` },
  { id: 'N5', tier: 'fast', task: `N5 "World and production views" (D2 §6 kinds 5-14). src/Meitou.Net/Replication/Views/ViewCodecs.World.cs and ViewCodecs.Production.cs: item codecs and view encode/decode for
buildings, ground items, containers, door picks, doors, inspected building detail, machines, research, crafts, unlocked buildings, projectiles if listed there - follow D2 §6's kind table
exactly; use N1's KeyedViewSync/KeyedViewDecoder (slot-based, CONTRACT §7). Find every nested type's fields in src/Meitou.Simulation. Writer-side limits. docs/net.md N5 section.
Tests: tests/Meitou.Tests/Net/WorldViewCodecTests.cs: round-trip of every kind with populated, non-default data, and keyed delta (insert, update, remove, reorder).` },
  { id: 'N6', tier: 'fast', task: `N6 "Player and detail views" (D2 §6 kinds 1-4 and 15-22, plus CONTRACT kinds 23 Relations, 24 Talkable, 25 Prospected; kind 1 Header = SquadAi, ActiveSquad, Viewer,
PlayerFaction, Money, InCreation, CreationRaces). src/Meitou.Net/Replication/Views/ViewCodecs.Player.cs and ViewCodecs.Detail.cs: trade, conversation, dialogue request, speech, towns,
camps, player squads, selected-character detail (Hud, HealthParts, ItemGrid, Skills, AllSkills, SkillXpMultipliers, Bounties, Path...) per D2. Use N1's KeyedViewSync where keyed.
Writer-side limits. docs/net.md N6 section. Tests: tests/Meitou.Tests/Net/PlayerViewCodecTests.cs: round-trip of every kind with populated non-default data.` },
  { id: 'N7', tier: 'strong', task: `N7 "Looks" (D2 §6 Looks; CONTRACT §2b RecordingAppearances, AppearanceRecipe, LookRegistry, ILookResolver, LookCache). src/Meitou.Net/Replication/Looks/*: record each
IAppearanceSource.Create call as a recipe (rolled seed or full loadout / editor FcsRecord appearance / shape / limbs), FromLook fallback, recipe codec (LookDefinition body), host
LookRegistry (stable look ids, never reused), client LookCache rebuilding looks (off the frame thread if the source allows; else synchronous with a note). Read
src/Meitou.Simulation/CharacterAssembly.cs, GeneratedAppearances, IAppearanceSource and src/Meitou.Data/Characters/CharacterAppearance.cs carefully. docs/net.md N7 section.
Tests: tests/Meitou.Tests/Net/LookTests.cs: recipe codec round-trip (no install), and one [Slow] test (skips without the install) that Rolled and Kit recipes rebuild the host's
parts/meshes from the real game data.` },
  { id: 'N8', tier: 'fast', task: `N8 "Events codec" (D2 §8; CONTRACT §2b Events, conflict rows C11-C13). src/Meitou.Net/Events/*: NetEvent records (SoundNet, BloodNet, FloaterNet, MessageNet, KnockoutNet,
ChatNet(From, Name, Text), NoticeKind/NoticeNet; kind 6 reserved), EventBatch, EventCodec, EventFilter (IsSpatial, Wants using N1's ViewState and an interest set). Find SoundEvent,
BloodMark, Floater, StealthKoEntry fields in src/Meitou.Simulation. Writer-side limits. docs/net.md N8 section. Tests: tests/Meitou.Tests/Net/EventCodecTests.cs: every kind
round-trips; spatial filter cases (inside/outside radius, interest overrides, non-spatial always wanted).` },
  { id: 'N9', tier: 'fast', task: `N9 "Protocol and handshake" (D3 §3, D4 §4; CONTRACT §2b Protocol). src/Meitou.Net/Session/{ProtocolCodec,Handshake,DataSetIdentity,PlayerColourRule}.cs: codecs for Challenge,
Hello, Welcome, Reject, Ready, WorldReset, RulesUpdate, ChatRequest, ClockRequest, ClockState, RosterEntry lists, NetRules, SessionRules, WeatherSync; Handshake.Proof (HMAC-SHA256
of password over nonce+guid), CheckRequest, CheckHello (order and reason texts per D3 §3), SanitizeName, AnimationHash; DataSetIdentity.Compute(LoadOrder) and Difference (find how the
game builds its load order in Meitou.Core ModList / Meitou.Data); PlayerColourRule.Assign. docs/net.md N9 section. Tests: tests/Meitou.Tests/Net/ProtocolCodecTests.cs (every
message round-trips incl. WorldReset/SessionRules/WeatherSync) and tests/Meitou.Tests/Net/HandshakeTests.cs (each reject code path, proof match/mismatch, name sanitizing, colour rule).` },
  { id: 'U1', tier: 'strong', task: `U1 "UI plumbing and ownership filter" (D5 §0-§1 and WP-E; CONTRACT §2b "Game ↔ UI", conflict rows C22-C28). Files per CONTRACT §4 U1 row: Meitou.Ui/MyGuiScreenHost.Keyboard.cs
(public Focus(screen, widget)), Meitou.Engine/Input/InputBindings.cs (Chat and PlayerList actions with defaults per D5), SettingsMenu.Descriptions.cs, UserConfig.cs (the §2b keys, EnsurePlayerId,
LoadOrCreate(file), RecentGames), create src/Meitou.Game/Multiplayer/{NetUiContract,PlayerColours,JoinAddress}.cs, PlayerInterface.Net.cs and the stub partial files
PlayerInterface.{Multiplayer,Chat,Players,NetStatus,NetMenu}.cs (empty partial hooks wave-3 packages fill), and the hooks in PlayerInterface.{Ui,Faithful,Frontend,Hud,cs,Picking,Portraits,Squads}.cs
and GameHost.Interactive.cs (Config, PlayerListHeld, F10 cheats gate). The own-character filter: squad bar/portraits/selection only for the viewer's own characters (CharacterSnapshot.Owner vs
the local player number; in single-player everything stays identical). docs: meitou-ui.md, game/ui-input.md, faithful-ui-parity.md, game/ui-screens.md as the contract says.
Tests: tests/Meitou.Tests/Ui/MultiplayerUiTests.cs: JoinAddress.TryParse cases (host, host:port, [v6], [v6]:port, bare v6, bad input) and PlayerColours/colour assignment.` },
  { id: 'G5', tier: 'fast', task: `G5 "Locked rule rows on clients" (D4 §5; CONTRACT §2b SettingsMenu HostRules). SettingsMenu.cs and SettingsMenu.Drawing.cs: Entry.Locked, the HostRules init property
(Func<SessionRules?>?), rule rows (attack slots, late defences, doors close behind, the off-screen switches, campaign ancestors, luckyren) read the host's SessionRules and are drawn locked
when HostRules returns non-null. Single-player unchanged. No new tests.` },
]

const pkgModel = p => p.tier === 'strong' ? undefined : 'sonnet'

const results = await pipeline(PACKAGES,
  p => agent(COMMON + '\nYOUR PACKAGE: ' + p.task, { label: `impl:${p.id}`, phase: 'Implement', schema: RESULT, isolation: 'worktree', model: pkgModel(p) }),
  (impl, p) => {
    if (!impl) return null
    return agent(`You are an adversarial senior reviewer for work package ${p.id} of the MeitouClient multiplayer effort.
The implementation is committed in the git worktree ${impl.worktree} on branch ${impl.branch} (commit ${impl.commit}). Work ONLY in that directory (cd into it in every
Bash command). Its base is the 'multiplayer' branch: review 'git diff multiplayer...HEAD'.
Package task given to the implementer:
${p.task}
Implementer's summary: ${impl.summary}
Implementer's deviations: ${impl.deviations}
Implementer's test notes: ${impl.testNotes}
Read ${SPEC}\\CONTRACT.md (authoritative, incl. §7 wave-1 notes) and the detail spec sections for this package. Hunt for real defects: contract mismatches (names, signatures,
wire layouts and semantics other packages rely on), single-player behaviour changes (golden pins, save bytes, UI flow), hash/determinism slips, reader/writer asymmetry
(a writer emitting what its reader refuses), missing writer-side limits, unbounded allocations from untrusted input, null/ordering/threading bugs, files touched outside
the package, docs missing or wrong, tests not in the approved list, style mismatches. Verify each suspicion against the code before acting. Fix every confirmed defect
directly in the worktree, then rebuild and rerun quick tests (${SDK}; 'dotnet build -m:2'; 'dotnet test --no-build --filter "Category!=Slow&Category!=Bench"'; ignore the
known baseline failure MyGuiScreenTests.ListsScrollSelectAndClipAndTabsHideInactiveContents). Commit fixes separately ("Multiplayer review: ..." + blank line +
Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>). Return the structured verdict with the final HEAD commit.`,
      { label: `review:${p.id}`, phase: 'Review', schema: REVIEW, model: pkgModel(p) })
      .then(r => ({ id: p.id, impl, review: r }))
  })

return results
