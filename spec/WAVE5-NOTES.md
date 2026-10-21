# Wave 5 as built (on `multiplayer`: T1 317f8d0, G1 e903d93)

Upstream master had no new commits. A concurrent session pushed b4dff43 (NetHost never throws on client input or unencodable
host data); T1 is rebased on it.

## T1 in-process host+client tests

`tests/Meitou.Tests/Net/SessionHarness.cs` + `SessionTests.cs` (6 tests). The harness: a host `WorldSession` (SyntheticTown:
population, player and movement systems, `StartPlayer` with start `40-t`, `PublishSnapshots = true`) or any prebuilt session,
`NetHost` and `NetClient`s over one `LoopbackNetwork` (10 ms) on a `ManualNetClock`, frames of 1/30 s in the game's order
(host Begin/Advance/End, then each guest Begin/Advance/End). Guests build their replica on `WorldReset` (`ReplaceWorld([],
MakeReplica)`, clock from the anchor, `Attach`, `Ready`) and view the town centre at draw distance 8000. After each host
EndFrame the harness records `PublishFor(guest)` by tick.

Cases: own MoveOrder moves (host and replica), foreign refused with `CommandGate.NotYours`; newest assembled snapshot ==
host's `PublishFor(1)` at that tick (ids, quantized wires, owners, selection, squads, header facts); isolation (the
MultiplayerWindowsTests data: host talks to Anna, guest trades with the seller; selections) — this one uses that data, not
SyntheticTown, because SyntheticTown has no dialogue or shops; clock (guest pause, host resume, HostOnly refusal); reconnect
(Away, same number and squads, no new squad, full snapshot); join while paused (squad in creation, host tick unchanged).

Changes outside the two files: `NetClient.LastAssembled` (internal), `MultiplayerWindowsTests.Data()` made `internal`.

## G1 game net plumbing

`GameHost.Network.cs` (role/netHost/netClient/netView/netAddress, `HostOnStart`/`JoinOnStart`/`NameOverride`, `NetIdentity()`
with the data set hashed once on a worker started by the first request, `NetBeginFrame`/`NetEndFrame`, request dispatch
returning "quit", `NetStatsLine`, `NetTitle`, partial cores), `GameHost.News.cs` (WorldNews moved; forwarding; `KnockoutText`),
`Multiplayer/NetSessionView.cs`; `GameHost.Interactive.cs` (two loop lines, `MultiplayerRequested` subscription, F11 line,
title, `HostRules`); docs/engine.md "Multiplayer" with Host/Client placeholders.

Additions beyond the contract: `EnterNet(NetHost)`, `EnterNet(NetClient, address)`, `ExitNet()` (shared by G2/G3; also make the
fields assigned so the package builds with warnings as errors). `NetSessionView.Loaded` (G3 sets it after "finishing";
`Playing` phase needs it) and a settable `Ended` (G2 sets it when hosting stops).

Remaining: until wave 6 the requests reach empty partial cores (the window would wait forever); single-player frame unchanged
otherwise (two null-checked calls). Nothing here can be run without the game.
