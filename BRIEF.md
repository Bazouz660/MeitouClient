# Multiplayer for MeitouClient: design brief

This branch (`multiplayer-design`) holds the design of multiplayer, not code. `research/` is the input gathered on 18 November 2026;
`design/` is where the design goes. The engine's code is on `master` of https://github.com/VlcekM/MeitouClient (this brief was written
against `315d26ecf`).

## What the project owner wants (his decisions, 18 November 2026)

1. **A headless, server-authoritative server.** The server owns the world. Clients send what the player wants and show what the server says.
2. **One design from two friends to hundreds of players.** Co-op with a friend must be easy; "massive" servers with hundreds of people must
   be possible later with the same design.
3. **Three ways to play, as in Minecraft.** An internal server inside the host's own game for local play, joining a friend through Steam,
   and a dedicated server.
4. **Everyone roams the whole world on their own**, and the server keeps everything in sync "in a smart way".
5. **Players are like NPC factions to each other.** Each player has squads as in single-player (several squads are allowed). Another
   player's squads do not appear in your squad lists, just as NPC squads do not. A player can join another player's faction, and player
   factions can be allied, neutral or hostile.
6. **Mods are supported** (data mods and code mods), and the server is **extensively configurable**.
7. **Gameplay settings belong to the server**: the ones on the settings pages' Fixes and Advanced tabs and the gameplay page.
8. **The save is the server's.**
9. **Who simulates at scale: the server only, first.** Players' machines hosting areas (what Anarchy's mod does, see
   `research/anarchy-notes.md`) is at most a later, optional server setting.
10. **Time:** a multiplayer setting enables voting to change the game speed. A large server turns it off and the speed stays at 1x.
11. **Offline players:** their characters leave the world; their base stays. (He asked for an opinion; the design should settle the
    details: what "leave" means for characters in a fight or in a cage, what an unattended base does, whether it can be raided.)
12. **First milestone: real networking, fully playable.** A whole playthrough with a few friends must work. Scale comes later.

Earlier in the day he also said: it must be easy to use for a player.

## What is already known

- `research/research-code.md`: what the code assumes today, with places: one player faction used in about 95 files, commands with no
  sender, one camera focus for the simulated area, no host without a GPU, weather driven by the camera, snapshots built for one viewer,
  about 40 gameplay settings in the client's config, the code-mod contract inside the game executable, no network code.
- `research/state-inventory.md` and `.tsv`: every state member of the simulation with its writers, its readers in the game, and whether the
  hash, the save and the published snapshot take it.
- `research/research-web.md`: how other games keep a large world in sync, with sources.
- `research/anarchy-notes.md`: what the existing Kenshi multiplayer mod does and why.
- `research/steam-notes.md`: what Kenshi's Steam library offers.
- Capacity: a tick costs about 1.5 ms plus 4.5 microseconds per loaded character at rest (docs/bench.md on master), so one process holds
  about 7,000 loaded characters at 30 ticks a second. That is enough for the first milestone and not for hundreds of players apart; the
  design must keep the door open for several simulators (area hosts) without building them now.

## What the design must deliver (`design/`)

Write for the engineers who will build it and for the owner who must approve it. Every statement about today's code needs its place
(file and line on master); mark what is a proposal and what is a fact. Keep each file short enough to read in one sitting.

1. `00-overview.md`: the architecture on one page, the terms used, what is in the first milestone and what is not.
2. `01-server-host.md`: the world host without a window or GPU: what moves out of `WorldSession` and `GameHost` (clocks, zone and navmesh
   streaming, weather on the game clock, saving, settings), the internal server inside the game versus the dedicated program, threads.
3. `02-players-and-factions.md`: from one `World.Player` to many players: a player's state (selection, purse, research, map knowledge,
   open windows, held item), player factions as real factions with relations that can change, joining a faction, several squads per player,
   crimes and bounties, recruiting, bases and ownership, raids. Include the migration path for the roughly 200 files that assume one
   player, in steps that each keep single-player working and the golden hash tests unchanged.
4. `03-commands-and-authority.md`: commands with a sender, the permission check, per-player trade, dialogue and inventory sessions, what
   replaces the commands that today are applied while the world is paused, speed voting and pause.
5. `04-replication.md`: what a client needs and how it gets it: the shared view and the personal view, interest by zone, changes instead
   of whole snapshots, priorities under a bandwidth budget, events (sounds, floating text, blood), joining late, what the client
   interpolates, and how the game's 2,670 direct reads of live simulation state are replaced. Give bandwidth estimates.
6. `05-areas.md`: which parts of the world are simulated when several players are apart, the cost, the limits and what the server does
   under load; the interface that would later let several simulators share the world.
7. `06-transport-and-join.md`: one protocol over three transports (in-process, UDP by address, Steam's old peer-to-peer API under Kenshi's
   library), the handshake, the version and data checks, identity, bans and whitelist.
8. `07-settings-mods-saves.md`: the server's configuration and its administration, which settings stay with the client, the mod list and
   its check on join, code mods on a server and on a client, the server's save and each player's part of it, offline players and bases.
9. `08-milestones.md`: the ordered work packages up to "a playthrough with a few friends", each with its scope, the files it touches, its
   risks, what must stay unchanged in single-player, and the tests it would need (as proposals: the owner approves every test).
10. `09-open-questions.md`: what the owner still has to decide, each with a recommendation and its cost.

## Rules

- This is design. Change no engine code, open no pull request, and write only on this branch.
- The project's own rules are in `CLAUDE.md` on master. Two that matter here: behaviour matches the original game unless a difference is an
  option, and parity work never brings back the original's loading pauses.
- Commit messages are one line with no body and no attribution of any kind.
- Earlier multiplayer work on the fork (branches `multiplayer` and `multiplayer-notes`, October 2026) was a throwaway test. Its decisions
  bind nothing. Its record of what Kenshi's Steam library offers (`docs/net.md` on `multiplayer`) is still a useful fact sheet.
