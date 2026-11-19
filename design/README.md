# Design

Multiplayer for MeitouClient, written 19 November 2026 against upstream `master` at `a1ef2e06f`. Start with the overview.
`../BRIEF.md` holds the owner's decisions; `../research/` the input.

| File | Covers |
|---|---|
| [00-overview.md](00-overview.md) | The architecture on one page, terms, departures from the outline, the first milestone, capacity |
| [01-server-host.md](01-server-host.md) | The world host without a window: what moves out of the game, three ways to run, threads |
| [02-players-and-factions.md](02-players-and-factions.md) | From one player to many; player factions and relations; the migration path |
| [03-commands-and-authority.md](03-commands-and-authority.md) | Sender, the permission gate, per-player sessions, time and votes, what replaces pausing |
| [04-replication.md](04-replication.md) | Shared and personal views, interest, changes under a budget, events, joining late, bandwidth |
| [05-areas.md](05-areas.md) | What is simulated, cost and limits, load shedding, the boundary for several simulators |
| [06-transport-and-join.md](06-transport-and-join.md) | Three transports, handshake, data check, identity, access control |
| [07-settings-mods-saves.md](07-settings-mods-saves.md) | The server's rules and administration, mods, the save, offline players and bases |
| [08-milestones.md](08-milestones.md) | Thirty ordered work packages with files, risks and proposed tests |
| [09-open-questions.md](09-open-questions.md) | What the owner must decide, each with a recommendation and its cost |
