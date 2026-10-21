# Wave 4 as built (on `multiplayer`: N11 ade5705, N12 9b839ac)

Both packages were written in one session directly on `multiplayer` (no worktrees). Upstream master had no new commits (5087ac3a
already merged). A concurrent session had pushed 196ff29 (gate: natural-rock ids must be placed rocks); wave 4 is on top of it.

## N11 NetHost

Files: `src/Meitou.Net/Session/Host/{NetHost,HostPeer,HostEventTap,HostWorldLink,NetHostOptions,IHostGame}.cs`, docs/net.md "Host".
Tests: `Net/EventTapTests.cs` (3), `Net/NetHostTests.cs` (6: accept, wrong password, protocol, full, duplicate GUID, kick + same number back).

Signatures beyond / differing from CONTRACT §2b:
- `NetHost.Epoch`, `NetHost.Log` (diagnostics), public consts (rates, texts such as `SignedInElsewhere`), `internal static CleanChat`
  (the client uses it too).
- `NetPlayerInfo` has `Colour` (as the contract says; D3 lacked it).
- **`ClientReplicator.Reset(ushort epoch, int player)`** added in N10's file: a world swap can change a connected player's
  number and a new replicator would restart the connection's string tables (the client's tables are per connection).
- NetHost owns and disposes the transport. `Stop` polls once so local drops are processed; it raises no Left notices.

Deviations / engine choices:
- Number for a GUID: world roster, else an away roster entry's number when the world has no session for it, else
  `NextNumber` skipping roster numbers. A number 0 for a remote GUID → `Duplicate`.
- Full is checked again at Hello (`others + 1 >= MaxPlayers`), not only at the connect request.
- ClockState is not sent to peers that owe a WorldReset (the reset carries the clock). `ClockChange.Speed` for a pause is the
  speed to resume at.
- A `SetSpeed` request with a speed outside 0/1/2/5 is ignored (SimulationClock would throw).
- Events: batches of ≤ 256 events, halved until under `MaxReliableMessage`. Loading peers get targeted events only.
  While `hostBusy` the targeted events wait (only Control traffic goes out).
- `Ban` of an away player bans the GUID and announces `Banned`.
- Host-side `Notice`/`Chat`/`ClockChanged` events are raised synchronously from inside `BeginFrame` (Poll) or the call.

Remaining:
- No in-process host+client test yet (T1, wave 5).
- Address bans are not used (`addressBanned: false`); bans are GUID-only per contract.
- Weather is polled from `IHostGame.Weather()` every EndFrame while a peer is in game (cheap by contract; G2 must keep it so).

## N12 NetClient and JitterBuffer

Files: `src/Meitou.Net/Session/Client/{NetClient,JitterBuffer,ClientEvents}.cs`, docs/net.md "Client and jitter buffer".
Tests: `Net/JitterBufferTests.cs` (7: steady, jitter, loss/cap/hold, tick jump snap, teleport, pause, speed change).

Signatures beyond the contract: `NetClient.Clock` (last ClockState), `NetClient.Log`; `public readonly record struct Presentation`;
`JitterBuffer` is public (`Add(tick, arrivalUs, snapshot)`, `SetRate`, `Advance`, `Clear`, `DelaySeconds`, `Starved`, `Count`,
`Rate`, `NewestTick`, static `Unteleport(older, newer)`); `ClientEvents` internal.

Deviations:
- Steering gain 2 (D3: 0.5) and slow-down limit 50 % (D3: ±10 %; speed-up stays +10 %), and no counted starvation within 1 s
  after a rate change: with D3's numbers a 1x→5x change starved ~9 times and grew the delay to 0.26 s (measured in a debug run).
- Non-spatial events are raised whatever the epoch (chat/notices may overtake the WorldReset on another channel).
- `Leave` does not raise `Disconnected` (the caller knows); host-caused or protocol ends do.
- A drop before the connection opened (timeout) or a socket error → `Unreachable` ("Could not reach the host.").
- Commands: untagged (local-only) and host-only types are never sent, debug types only with `Rules.Cheats` (the host
  drops a peer that sends a debug command with cheats off).
- The WorldReset's weather is not raised as `Weather`; the game applies `reset.Weather` while building the replica (G3).

Remaining:
- NetClient is exercised only by T1 (wave 5).
- `LookCache` builds looks on the thread pool (background: true).

## Review fixes after wave 4 (session ccd542, on `multiplayer`)

- b4dff43: `NetHost` never throws on client input or unencodable host data. `OnReceived` catches every exception (not
  only `NetFormatException`) and drops that client with `ProtocolError`; a WorldReset whose weather the codec refuses goes
  without it (non-finite focus → 0); refused Weather or EventBatch writes are logged (`TryWrite`), never thrown out of `EndFrame`.
- 196ff29 (before wave 4): the gate admits a natural-rock site id only in `ProductionSystem.NaturalId`'s exact form for a
  placed rock of the rock source (`ProductionSystem.IsRock`); without a rock source none passes. Done: the CONTRACT
  "bound or validate the natural-rock site check" item.
- `wip/wave4-alternate` (965c1bc): an independent N11/N12 built in parallel by this session, not merged; kept only for comparison.
- Left as is: the host's `pendingChange` keeps only the last clock change of a frame (two changes in one frame announce one);
  the client's Hello throws `ArgumentException` out of `BeginFrame` for a data set over `ProtocolCodec.MaxDataFiles` files.
