const PACKAGES = [
  { id: 'N11', tier: 'strong', task: `N11 "NetHost" (D3 §4 as amended by CONTRACT §2b "Host and client" [N11], §3 flows: host start, client join steps 2 and 4, command path, snapshot path (host side),
events path (tap), clock path (authority), chat, disconnect/reconnect, world swap while hosting; conflict rows C10, C14, C16-C19, C26, C27, C31, C35, C37, C39, C41, C42).
Files: src/Meitou.Net/Session/Host/{NetHost,HostPeer,HostEventTap,HostWorldLink,NetHostOptions,IHostGame}.cs and docs/net.md "Host" (replace its Pending line). Use the as-built
transport (N3: INetServerTransport incl. Port, PeerSink, MaxDropPayload/MaxReliableMessage limits), protocol (N9: ProtocolCodec, Handshake.CheckRequest semantics where 'connected'
excludes requester and host, CheckHello, SanitizeName, AnimationHash, DataSetIdentity, PlayerColourRule), commands (N2: CommandBatchCodec, CommandCodec with allowDebug), events
(N8: EventBatch, EventCodec, EventFilter; split batches so each message stays under NetLimits.MaxReliableMessage), replication (N10: ClientReplicator, ReplicaCatalog,
SnapshotEncoderOptions, ClientFrameCodec, LastSend stats), the simulation (CommandGate.Admit, World.PublishFor, Player.Join/FindByGuid/NextNumber, JoinPlayer enqueued at every Ready,
PlayerLeft enqueuing CloseTrade/EndConversation/CloseContainer with Player = n, TakeMessages is drained by the GAME (G1), not by NetHost) and the engine (WorldSession.ClockAuthority,
RequestClock, SimulationTicked, WorldReplaced -> epoch++). NetHost must be fully drivable headless (no Game references): BeginFrame/EndFrame from the caller's frame thread, all
LiteNetLib events only inside Poll. Rate-limit per CONTRACT (commands 200/s burst 400, refusals <= 2/s, chat 5 lines/5 s, ClockState every 2 s or on change). Tests (approved):
tests/Meitou.Tests/Net/EventTapTests.cs (the host's own consumers still get every sound/blood/floater event while the tap forwards copies; restore across ReplaceWorld) and a
NetHost handshake test over LoopbackNetwork with a hand-driven fake client (accept, wrong password, wrong protocol, full, duplicate GUID, kick) in tests/Meitou.Tests/Net/NetHostTests.cs.` },
  { id: 'N12', tier: 'strong', task: `N12 "NetClient and jitter buffer" (D3 §5 as amended by CONTRACT §2b [N12], §3 flows: client join steps 1-3 and 5, command path (uplink), snapshot path (client side), events
path (client release timing), clock path (client follow), chat, disconnect). Files: src/Meitou.Net/Session/Client/{NetClient,JitterBuffer,ClientEvents}.cs and docs/net.md "Client"
(replace its Pending line). Use the as-built N3 transport (INetClientTransport), N9 protocol codecs and Handshake.Proof, N2 CommandBatchCodec (drain session.World.Commands with
TakeDue(long.MaxValue), batches <= 64, per-client seq, only while Playing), N8 events, N10 ClientMirror (Receive per channel, TryAssemble, WriteFrame acks + ViewState) and the
replica world API (WorldSession.Mode Replica, session.Present(previous, current, alpha, motionAlpha), World.Present). JitterBuffer: frames are assembled WorldSnapshots keyed by tick and
arrival time; adaptive playback delay (~100 ms default, grows with jitter, shrinks slowly), presentation rate 30 x AchievedSpeed (0 when paused or HostBusy), alpha between the two
snapshots around the playback point, MotionAlpha up to 2 when starved (extrapolation cap) then hold, snap on a large jump (teleport) or epoch change, Starved counter and DelayMs for
stats. Clock follow per CONTRACT §3 "Clock path" (session.Clock moved from the anchor; ClockChanged raised for changes with Player >= 0). Spatial events released into the replica
world's Sounds/BloodMarks/Floaters queues when the presented tick reaches batch.Tick or after 1 s; non-spatial raised at once. NetClient must be drivable headless (no Game
references). Tests (approved): tests/Meitou.Tests/Net/JitterBufferTests.cs (steady stream, jitter, loss/starvation and extrapolation cap, teleport snap, pause, speed change).` },
]
