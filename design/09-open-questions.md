# 09. Open questions for the owner

Each has a recommendation, so work can start without an answer; an answer different from the recommendation costs what the last column
says. Most important first. Facts are from upstream `a1ef2e06f`.

## Decided by the owner (19 November 2026)

| # | Decision |
|---|---|
| Q1 | Built in the main repository. The owner is a core maintainer of it; the packages go upstream as ordinary pull requests. |
| Q2 | Milestone 1 ends with separate player factions (package 29, then the hardening of 30). The point after package 24 stays as a checkpoint to test with friends, not a release. |
| Q3 | Both camera modes: free in single-player and on Friends servers, leashed on Community and Large, as recommended. (His answer was "both"; this is how it was read.) |
| Q4 | The offline defaults as recommended. |
| Q5 | One purse per player. |
| Q6 | The speed vote rule as recommended. |
| Q7 | Accepted: a dedicated server needs a Kenshi install and starts as the game executable with `--server`. |
| Q8 | LiteNetLib, behind our own `ITransport`. |
| New | Players can trade with each other: items, cats and characters ([03](03-commands-and-authority.md#trade-between-players); package 19a of [08](08-milestones.md)). |

## Decisions that shape the plan (Q1 to Q8 are decided above; kept here for their reasons)

| # | Question | Recommendation | Why | If you decide otherwise |
|---|---|---|---|---|
| Q1 | **Where is this built?** Upstream's CONTRIBUTING.md says it is "not accepting large features or new systems" and that larger refactors are not accepted. Stage A of [08](08-milestones.md) is fifteen refactors of the core. | Ask the upstream maintainer before package 1, showing stage A only: every package there changes no behaviour and removes duplication he already has (two frame loops repeating the host wiring; the tool's copy of zone streaming and settings). A maintainer already described this shape (a dedicated server plus a local one started by "host", `../research/anarchy-notes.md`), and upstream docs mention "planned native multiplayer" (docs/simulation.md:271). | Building on a fork means rebasing fifteen core refactors onto a master that moved 82 commits in one day. | A fork: budget a standing merge cost and expect stage A to conflict constantly; do stage A as late and as fast as possible instead of package by package. |
| Q2 | **Is milestone 1 done at the checkpoint (friends in one faction, package 24) or only with separate player factions (package 29)?** | Call the checkpoint "milestone 1a, playable" and release it to friends; keep factions in milestone 1 as 1b. | The first stage touches about 110 places, the second about 310 more plus the relations table. Friends playing early finds the real problems. | If factions must come first, packages 25-27 move before 16; nothing is playable by others for longer. |
| Q3 | **May a player's camera load any place on a server?** Today the camera loads a 3 x 3 block wherever it is. | Free for single-player and Friends (as today). Leashed to within 6000 of the player's characters on Community, 3000 on Large. A leashed camera still flies anywhere but sees no characters beyond the leash. | Each free camera can cost the server 150-500 characters and shows a player what is happening anywhere. | Free everywhere: the 32-player row of [05](05-areas.md#cost-and-limits) moves toward its high end and beyond, since nothing bounds how many places are loaded. Leashed everywhere: scouting with the camera stops working in co-op, a visible change from the original. |
| Q4 | **Offline rules: defaults.** [07](07-settings-mods-saves.md#offline-players-and-bases) | Logout delay 30 s; in danger up to 10 min; captives safe; no healing while away; unattended base: no production, full protection starting 5 min after the last hostile leaves; "unattended" means no member **online** (not "none nearby"). | Leaving must not be an escape or a free hospital; a sleeping player should not lose a base. Tying production to "online" keeps today's rule that your base works while you travel. | Every item is a rule, so another default costs nothing. Tying production to "a member is near" instead changes single-player behaviour unless made a new switch. |
| Q5 | **One purse per player, or per faction as in Kenshi?** | Per player, with `GiveMoney`. A faction-wide purse as a later faction option. | With several people in a faction a shared wallet lets anyone spend it all. | Shared: simpler (it is today's field), but needs spending rights by role before strangers share a faction. |
| Q6 | **The speed vote rule.** | Anyone may lower the speed or pause at once; raising needs everyone (or a majority after 20 s); pauses end by themselves after 5 min; a server with nobody online stops its clock. Large servers: fixed 1x, as you decided. | Slowing down is what a player in trouble needs right now; speeding up is what must be agreed. | A plain majority vote both ways is the same code with another table. |
| Q7 | **The dedicated server needs a Kenshi install** (it reads data, levels, collision and animations: `src/Meitou.Simulation/WorldBoot.cs:115`), and its first form is the game executable started with `--server`. | Accept both. Document that a server operator must own Kenshi and copy or install it on the server machine. | The simulation uses the original's files; there is no way round it. Starting from the game executable makes every existing code mod load on a server unchanged. | A separate slim executable first: move `Meitou.Modding` out of the Game assembly before package 6 (a mod-contract change, API version bump). |
| Q8 | **A network library.** No networking package exists upstream. | Take LiteNetLib (MIT, pure C#) for UDP reliability, behind our own `ITransport`. | Reliable UDP is weeks to write and months to trust. | Own implementation: add a large package before 17 and its own test plan. |

## Decisions that can wait until their package

| # | Question | Recommendation | If you decide otherwise |
|---|---|---|---|
| Q9 | **Should a server send mod files to a joining client?** | No in milestone 1. Workshop mods are fetched by subscribing; others are named for the player to install. | Sending files: a download channel, disk writes on clients from an untrusted server, and redistribution of mods whose authors did not agree. |
| Q10 | **Identity without accounts.** Steam id over Steam; a locally made key pair over UDP; no account server; an admin links the two. Traffic is not encrypted in milestone 1 (the password is not exposed). | Accept. Add encryption right after milestone 1. | Accounts with passwords per server (as the existing Kenshi multiplayer mod does): a password store on the server and a login screen. |
| Q11 | **Server saves and the original game.** A server save opens in single-player Meitou; it is not promised to open in original Kenshi once it holds more than one player faction. On a dedicated server player 0 is a reserved, empty player. | Accept. | Keeping original-loadable saves means writing other players as an NPC faction the original knows, which loses ownership on reload. |
| Q12 | **Local weather effect zones (dust storms, gas, burning) come from the renderer's particles today** (`src/Meitou.Game/Host/WorldSync/WeatherFeed.cs:53-72`), so a server has none. | Milestone 1: region weather (acid rain, wind) is authoritative; local zones act only on an internal server, near the host's camera, as today. Then read how effect units are placed and derive them in the host. | Doing it first: one more package before 6, of unknown size until `Data/Particles` is read. |
| Q13 | **Raids with many players.** One campaign at a time per NPC faction, so raids do not multiply with players. | Keep for milestone 1. A rule for one manager per (NPC faction, player faction) later. | Per pair from the start: campaign state and its save keys (WAR_SAVESTATE) change. |
| Q14 | **One world story.** World states that ask "did the player kill or capture X" are true when any player did. A unique character recruited or killed is gone for everyone. | Accept: it is one world. | Per-faction world states would fork town overrides per viewer, which a shared world cannot show. |
| Q15 | **Old code mods on a server.** A mod without `side` counts as `both`: server and every client must have it. Client-only mods: allowed on Friends, listed on Community and Large. | Accept. | Treating old mods as `server` would hide them from clients and break those with UI. |
| Q16 | **Friendly fire between non-hostile player factions.** | Off by default (blows do no damage); `turnsHostile` for servers that want it. Blows between player factions never move relations by themselves; diplomacy is explicit. | On: an accident in a shared fight starts a war. |
| Q17 | **Stealth against other players.** Kenshi shows every loaded character. | Milestone 1 shows everyone, as the original. Hiding unseen sneakers from enemy players is a later PvP rule. | Now: a per-viewer visibility test in replication and a decision about what "seen" means for a player's whole squad. |
| Q19 | **Trading characters on a public server.** | On for Friends; off by default on Community and Large (`trade.characters`). | On everywhere: trained characters get sold, which a public server may or may not want. |
| Q20 | **How far apart may two players trade?** | Within talk reach to open, offers from within 300 units; a money gift needs the same reach unless `trade.remoteMoney` is on. | At any distance: simpler to use, but items and characters cross the map in a tick. |
| Q21 | **May a player trade away their last character?** | No. | Yes: the player then has no character and must make a new start. |
| Q18 | **Players' machines simulating areas.** | Not designed here, as you decided. The area boundary of [05](05-areas.md) would allow a client as a simulator later; it adds the question of trusting its results. | - |

## Things only you can find out

- Whether Steam's "Join game" starts Kenshi or Meitou when the game is not running (`docs/net.md` on `multiplayer`: Unknown).
- Whether joining through Steam worked between two accounts on different networks, or only on one (your report says it worked; the fact
  sheet did not record it as verified).
- The CPU and thread count behind the bench line "1.5 ms + 4.5 µs per character" (docs/bench.md:179 names neither). Every capacity number
  in this design scales with it.

## What this design could not check

Collected from the "Not checked" sections; none blocks stage A.

| Area | Unknown |
|---|---|
| Cost | Tick scaling with threads; memory per character and per zone; save capture time; navmesh pre-bake time; replication CPU |
| Wire sizes | Appearance and ragdoll sizes; how often animation layers change |
| Code | `ModHost` internals; production command handlers' owner tests; WorldMap and WorldPicking live reads; how effect particle units are placed; what creates player-faction platoons |
| Steam | Auth tickets and lobbies in Kenshi's library; the overlay over Vulkan; two-account NAT and relay |
| Original game | How it loads a platoon of an unknown faction |
