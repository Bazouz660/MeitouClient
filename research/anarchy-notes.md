# How the existing Kenshi multiplayer mod works (read 18 November 2026)

The mod is "Kenshi Multiplayer" by Anarchy (Steam Workshop item 3808257125, module name AnarchyKenshi; created 26 September 2026, updated
4 November 2026, public). What follows is from its Workshop page and from what its author wrote in the MeitouClient Discord on 4 November
2026, paraphrased.

## What its author says
- It is a relay peer-to-peer system, which he compares to Valheim. Its server does not simulate the world.
- Every client can take the zones near it and host them for the other players. The calculations run on the clients.
- The server relays the traffic (players do not see each other's addresses), checks it (cheating among other things), stores some data,
  picks hosts by their performance and moves a zone to another player when needed.
- He left the standard server-client model because it could not carry that many players and NPCs, and says plain peer-to-peer only suits
  co-op.
- His figures: about 100 players in normal use, more than 200 at once in a test, and 10,000 to 30,000 NPCs at once.
- He said he might move his multiplayer to MeitouClient once it is finished.

## What its Workshop page says
- One open world shared by up to 200 players on one server; accounts made in the game; join a faction or create one; a character that dies
  loses its skills; early access; the mod connects to the author's own servers; closed source.

## What a MeitouClient maintainer said in the same conversation
- Multiplayer here would probably be a dedicated server, plus a local server started on the same machine when a player clicks "host",
  along with the headless client that already exists for tests. He called the per-area host idea smart.

## What it means for this design
- That mod's scale comes from spreading the simulation over the players' machines. Its server is a relay and a referee, not the simulator.
  It has to be that way there: the original engine cannot run on a server.
- MeitouClient's simulation is its own and runs without a window, so a server can simulate. The cost limit still applies: about 7,000
  loaded characters per process at 30 ticks a second, against that mod's 10,000 to 30,000.
- So "server-authoritative" and "hundreds of players apart" together need the world cut into areas that several simulators can run. The
  owner decided those simulators are server processes first; players' machines are at most a later option.
