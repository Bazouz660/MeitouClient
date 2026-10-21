const PACKAGES = [
  { id: 'N10', tier: 'strong', task: `N10 "Interest, delta, replicator, mirror" (D2 §7 and the delta parts of §5-§6; CONTRACT §2b Replication [N10], §3 "Snapshot path"). src/Meitou.Net/Replication/Delta/*:
ClientFrame (+codec; ViewState already exists from N1 - do not redefine), interest set (all player-faction characters, characters within the client's draw distance of its eye/focus
with hysteresis, everything its UI refers to: selected/inspected, conversation/trade partners, carried/carrier pairs), ClientReplicator (per-client ring of sent parts, per-entity
baselines = last acked state within BaselineWindow, self-contained parts under IPacketSink.MaxUnreliablePayload, priority order within the byte budget, removals, projectiles,
look definitions before first use, views on the Views channel via the N5/N6 syncs with change detection by reference/revision/bytes, string-table confirmations from acks, Reset(epoch)),
ClientMirror (Receive per channel, part apply onto acked baselines, view commit when an assembled snapshot reaches the view's tick, TryAssemble into a full WorldSnapshot using N4 ToSnapshot,
N7 LookCache resolution, TickOf, WriteFrame with pending acks and Resync requests for undecodable entities). Use the as-built N4/N5/N6/N7 APIs (WAVE2-NOTES). Catch ArgumentException
from codecs for unencodable host data and log/skip that item instead of throwing out of Send. docs/net.md N10 section. Tests: tests/Meitou.Tests/Net/ReplicationTests.cs (seeded
drop/reorder/duplicate sink; after a quiet period the mirror equals the host's quantized view and views; removals, slot reuse, string-table loss, starvation recovery; bytes logged via
ITestOutputHelper) and tests/Meitou.Tests/Net/WireCoverageTests.cs (every public property of WorldSnapshot and CharacterSnapshot is carried by some codec group or view kind, by reflection
against an explicit coverage table, so a new snapshot field fails the test until it is wired).` },
  { id: 'U2', tier: 'fast', task: `U2 "Title entry and Multiplayer window" (D5 WP-A; CONTRACT §2b "Game <-> UI"). PlayerInterface.TitleMenu.cs and PlayerInterface.Multiplayer.cs (stub from U1): the title's
MULTIPLAYER entry (as U1 wired it) opens the Multiplayer window built as MyGUI layout XML parsed with MyGuiReader.ParseLayout using only installed skins/fonts: Host section (new game
start or a save from the load list, port, max players 2-8, password, clients control time, allow cheats) and Join section (address host:port parsed with JoinAddress, recent games list,
password), shared player name (persisted in UserConfig), Connect/Host/Cancel with a connecting status and readable errors; raises MultiplayerRequested with HostGame/JoinGame/CancelJoin;
OpenMultiplayer(runningGame) opens the Host section for the running game (Esc menu). Works in both Faithful and Meitou UI modes. Read the U1 code first. No new tests.` },
  { id: 'U3', tier: 'fast', task: `U3 "Chat" (D5 WP-B). PlayerInterface.Chat.cs and Multiplayer/ChatCommands.cs: Enter (InputAction.OpenChat, see U1) opens a non-modal one-line entry focused via
MyGuiScreenHost.FocusBox; Enter sends via IMultiplayerView.SendChat, Escape cancels; history lines fade at the bottom-left (player-coloured names via PlayerColours); system lines for
MultiplayerEvent Joined/Left/TimedOut/Kicked/Banned/Paused/Resumed/Speed ("You" for one's own change); host chat commands /kick <name> [reason], /ban <name>, /players, /help parsed in
ChatCommands (pure functions); only active when Net is non-null. Read the U1 code first. Tests: add the ChatCommands parsing cases to tests/Meitou.Tests/Ui/MultiplayerUiTests.cs.` },
  { id: 'U4', tier: 'fast', task: `U4 "Player list, name tags, map markers" (D5 WP-C). PlayerInterface.Players.cs (hold InputAction.PlayerList: overlay with each roster entry's colour, name, host marker, ping,
squads count, Loading/Away state), PlayerInterface.Stealth.cs (name tags: other players' characters show their owner's name in the owner's colour via CharacterSnapshot.Owner and the
roster; own tags unchanged; single-player tags exactly unchanged), PlayerInterface.Map.cs (markers for other players' squads in their colours). Read the U1 code first. No new tests.` },
  { id: 'U5', tier: 'fast', task: `U5 "Status, Esc menu, players window, alerts" (D5 WP-D). PlayerInterface.NetStatus.cs (connection quality indicator from IMultiplayerView.Quality: ping, warning when
snapshots starve; "<name> paused the game" in the paused panel via PausedBy; HUD time buttons disabled when !CanControlTime) and PlayerInterface.NetMenu.cs (Esc menu: clients get
Disconnect instead of Save/Load/New game; the host gets MULTIPLAYER (open to multiplayer) / Stop hosting and a Players window with Kick/Ban; ShowDisconnected(reason, reachedPlay) dialog
returning to the title: DISCONNECTED when reachedPlay else CONNECTION FAILED). Keep tests/Meitou.Tests/Ui/FaithfulInteractionTests passing (the contract notes the Esc -> Exit flow with
7 rows). Read the U1 code first. No new tests.` },
  { id: 'F3', tier: 'fast', task: `F3 "Review fix-ups from wave 2" (small product fixes the wave-2 reviewers flagged, in files no other wave-3 package touches): (1) src/Meitou.Net/Fcs/FcsRecordCodec.cs writer:
enforce the reader's limits on write (MaxName for map keys and string ids, string byte limits) and refuse non-finite float/vector/quaternion values on write with ArgumentException, so a
reader never refuses what a writer produced; add the failing-write cases to tests/Meitou.Tests/Net/FcsRecordCodecTests.cs. (2) src/Meitou.Game/Multiplayer/PlayerColours.cs: make its
assignment forward to Meitou.Net.Session.PlayerColourRule.Assign instead of duplicating the rule (keep its public API; U1's tests must still pass). (3) src/Meitou.Net/Session/ProtocolCodec.cs:
clamp the Reject reason so an encoded Reject fits INetTransport.MaxDropPayload (1000 bytes), and refuse non-finite clock/weather doubles on write; add cases to
tests/Meitou.Tests/Net/ProtocolCodecTests.cs. (4) src/Meitou.Net/Events/EventCodec.cs: validate enum values on write. Touch nothing else.` },
]
