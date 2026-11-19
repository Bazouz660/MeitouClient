# 06. Transport and joining

One protocol over three transports, the handshake, the checks, identity and access. Upstream `a1ef2e06f`; Steam facts from
`docs/net.md` on the fork's old `multiplayer` branch (a fact sheet from a throwaway test; cited as "net.md").

## Today (facts)

- No network code on master. No networking package among the dependencies (THIRD_PARTY_NOTICES.md:117-139).
- Steam on master: `SteamSession` loads the install's `steam_api64.dll`, Windows only, as app 233860, and reads the language
  (`src/Meitou.Game/Host/SteamSession.cs:34-146`); `SteamUgc` binds Workshop calls for the launcher (`src/Meitou.Core/Steam/SteamUgc.cs:115-124`).
  No friends, identity or networking calls. The game never pumps Steam callbacks in play.
- Kenshi's Steam library (net.md:238-248, **Verified** there from its export table): Steamworks of about SDK 1.37-1.41. It has
  `SteamNetworking005` (the old peer-to-peer calls: `SendP2PPacket`, `ReadP2PPacket`, `AcceptP2PSessionWithUser`,
  `AllowP2PPacketRelay`, ...), `SteamFriends015` with rich presence, `SteamUser019` `GetSteamID`. It has **no**
  `ISteamNetworkingSockets` or `ISteamNetworkingMessages`. Kenshi ships only a Windows library.
- Callbacks need a C++ `CCallbackBase` object; a native-memory shim with that layout worked (net.md:262-270, **Verified** there: 47
  callbacks dispatched).
- Peer-to-peer behaviour (net.md:272-278): send types unreliable, unreliable-no-delay, reliable (ordered per channel, up to 1 MB),
  reliable-with-buffering; unreliable packets up to about 1200 bytes. **Verified** there only by an account sending to itself: 40
  reliable packets up to 65,536 bytes in order, 50 packets of 1155 bytes. Between two accounts (NAT, relay, the accept callback) it was
  **Unknown** in net.md; the owner reports that joining a friend through Steam worked on that branch (`../research/steam-notes.md`).
- Not stated anywhere: auth tickets (`GetAuthSessionTicket`, `BeginAuthSession`), lobbies as flat exports, any game-server API. The
  design assumes none of them.
- Data identity today: file names in load order (`GameDatabase.Files`, `src/Meitou.Data/GameDatabase.cs:38`), fingerprints of code mods'
  data edits that are "the same on every machine" (`CodeEdits`, :74; `src/Meitou.Game/Mods/ModDataChanges.cs:92-101`), and a navmesh
  cache key that uses write times and absolute paths and so differs between machines (`src/Meitou.Simulation/Navigation/NavContentHash.cs:25-160`).
  **No content hash** of mod files, of the merged database or of mod assemblies exists.
- A session can already run on another mod list without touching the player's own: `--mods-cfg <file>`
  (docs/launch-options.md:19; `src/Meitou.Core/Content/ModList.cs:112-133`). Data loads once per process, so a different list needs a
  restart (`src/Meitou.Data/GameDatabase.cs:91`). The launcher can subscribe to and download Workshop items by id on Windows and macOS
  (docs/launcher.md:349-378).

## Target (proposal)

### Layers

```
 commands, views, events                 (03, 04)
 ─────────────────────────────────────
 session: handshake, channels, acks, baselines, budget, keep-alive
 ─────────────────────────────────────
 ITransport: send(peer, channel, bytes, reliable?), receive, connect, close
   ├ InProcess   two queues
   ├ Udp         by address and port
   └ SteamP2P    by Steam id, relayed by Steam when needed
```

A new library `Meitou.Net` references Core, Data and Simulation, never Game (the layering of CLAUDE.md). The host can listen on several
transports at once, so one co-op game takes a Steam friend and a friend by address together.

| Channel | Used for | Delivery |
|---|---|---|
| 0 State | character updates, effects | unreliable, newest wins |
| 1 Control | handshake, commands, refusals, globals | reliable, ordered |
| 2 Views | spawns, zone lists, personal view parts | reliable, ordered |
| 3 Bulk | join baseline, anything large | reliable, ordered, own budget |

Separate reliable channels keep a large personal view from delaying a command's refusal.

### The three transports

| | In-process | UDP | Steam P2P |
|---|---|---|---|
| Address | - | `host:port` (default port a rule) | `steam:<id64>` |
| Reliability | trivial | from the library below | Steam's reliable send for channels 1-3, unreliable for 0 |
| Packet size | any | 1200 B payload by default; fragments above | 1150 B unreliable (net.md's tested margin); reliable up to 64 KB per message, larger split by the session |
| NAT | - | the host opens a port, or uses UPnP if a later package adds it | Steam punches or relays (`AllowP2PPacketRelay(true)`); nothing to open |
| Platforms | all | all | Windows only |
| Used by | tests, `--loopback-net` | dedicated servers; friends by address | "Join game" in the Steam friends list |

**UDP reliability: use LiteNetLib** (MIT, pure C#, one NuGet package; the old branch used 2.1.4) rather than write and debug a reliable
UDP layer. It is licence-compatible with GPL-3.0-or-later and needs a line in THIRD_PARTY_NOTICES. The project has no rule against
packages but adds them sparingly; this is the owner's call ([09](09-open-questions.md), Q8). The session layer hides it behind
`ITransport`, so replacing it later touches one class.

**Steam** needs, in `Meitou.Core/Steam` beside `SteamUgc`: the interface accessors for `SteamUser019`, `SteamFriends015`,
`SteamNetworking005`; the callback shim; a `RunCallbacks` pump on the network thread while a session is open. Callbacks 1202 (session
request: accept only ids that are mid-handshake or allowed), 1203 (connect failed), 337 (join requested). The session frames its own
connect, accept, reject, disconnect and ping on top, as the old branch did (net.md:280-292), because the old API has no connection
object. Steam is started for a hosting or joining game exactly as today for a playing one (`Program.cs:78`); a dedicated server does not
start it.

### Joining, as the player sees it

| Way | Steps |
|---|---|
| Play alone | unchanged |
| Open my game to friends | Menu, "Open to friends": choose Steam friends and/or "by address" (shows the address and port), optional password. The game sets rich presence `connect` = `+meitou_join <my id>` |
| Join a friend on Steam | Right-click the friend in Steam, "Join game"; or the game's Multiplayer window lists friends who host. Callback 337, or `+meitou_join <id>` on the command line when the game was not running (**Unknown** in net.md whether Steam then starts Kenshi itself instead of Meitou) |
| Join by address | Multiplayer window, "Direct connect": address, optional password. Recent servers are remembered. `--join <address>` on the command line |
| Dedicated | The operator runs `MeitouClient --server`; players join by address |

No server browser in milestone 1.

### Handshake

```
 C -> S  Hello      protocol version, game version, wanted name
 S -> C  Challenge  nonce, server name, what identity it accepts, whether a password is set
 C -> S  Identity   identity proof (below), password proof = HMAC-SHA256(password, nonce), data identity
 S -> C  Welcome    player id, rules, mod list, tick, speed            | Reject  reason code + detail
 S -> C  (channel 3) join baseline  (04, "Joining late")
 C -> S  Ready      the client has loaded the place; start sending state
```

A client that does not reach Ready within a rule's time (default 120 s; loading a new place takes time) is dropped. Reject reasons are
specific, so the client can say what to do: wrong protocol, wrong game version (with the server's), full, banned, not on the whitelist,
wrong password, data differs (with the list), name taken.

### Version and data checks

| Check | Compared | On mismatch |
|---|---|---|
| Protocol version | one integer, bumped on any wire change | refuse |
| Game version | the release (nearest `v*` tag, as `gameVersion` ranges already compare, docs/modding-dependencies.md) | refuse by default; rule `allowOtherVersions` for testers |
| **Data identity** (new) | For each file of the load order, in order: name, length, first 8 bytes of SHA-256 of its content. Plus the same for the level files of each layer, the `CodeEdits` fingerprints, and `FixGround` (it changes terrain heights and so where characters stand; `src/Meitou.Data/World/HeightmapRepair.cs:42`) | The server sends its list; the client shows what differs: missing mods, extra mods, different versions, order |
| Code mods | For each code mod the server marks as needed on clients ([07](07-settings-mods-saves.md#code-mods)): id, version, SHA-256 of its assemblies | listed like data mods |
| Animations | a hash of animation names and lengths, as the old branch had (net.md, `AnimationHash`) | warn only: a wrong clip is a wrong picture, not a wrong world |

The identity is computed once at boot on a worker (hashing a large mod list is file reads the boot already does) and cached by name,
size and write time locally.

**When the data differs** the client offers, in this order:
1. "Play with this server's mods": it writes the server's list to a temporary `mods.cfg`, and restarts itself with
   `--mods-cfg <that file> --join <address>`. The player's own `data/mods.cfg` is not touched (that option is read-only by design,
   docs/launch-options.md:19). A restart costs a boot (**Observed** upstream 17.5-18 s to the menu with 655 mods, docs/engine.md:50).
2. If mods are missing and they are Workshop items (the server sends their ids): "Subscribe and download N mods" through the existing
   Workshop subscribe, then step 1. Windows and macOS; on Linux the player is told which to install.
3. If a mod is not on the Workshop: its name and version are shown. The server does not send mod files in milestone 1
   ([09](09-open-questions.md), Q9).

### Identity

There is no account server and none is proposed.

| Joined through | Identity | How it is proven |
|---|---|---|
| Steam P2P | the Steam id | Steam delivers the packets of a session with the sender's id; the host takes the id from `ReadP2PPacket`, never from the message. As trustworthy as Steam's session, which is enough for friends |
| UDP | a key pair the game makes on first use and keeps in the user's config (ECDSA P-256, in .NET's base library). The identity is the public key's fingerprint | the client signs the server's nonce |
| In-process | the local player | - |

A player is known to a server by that identity; the name is a label and can change. A player who joins once by Steam and once by address
is two players unless an admin links them (`player link`). A dedicated server cannot check that a player owns Kenshi: nothing in the
library seen so far verifies a ticket without a Steam user. In practice a client cannot run without the game's files.

Losing the key file loses the identity on address-joined servers; an admin can re-link. The config page shows the fingerprint and an
"export identity" button.

### Access control

All in the server's configuration ([07](07-settings-mods-saves.md)) and changeable by admins while running:
- **Password** (optional), proven by HMAC, never sent.
- **Whitelist** (off, or a list of identities; "friends only" for Steam hosting accepts ids that are the host's Steam friends).
- **Bans** by identity and by address, with reason and expiry. A banned Steam id is refused in the session-request callback, before any data.
- **Admins** by identity. The host of an internal server is always one.
- **Limits**: maximum players; connections per address; handshake attempts per minute per address (a bucket), so the handshake cannot
  be used to make the server hash or sign in a loop.

### Security, plainly

| Threat | Milestone 1 | Later |
|---|---|---|
| A client sends commands for things it does not own | refused by the gate ([03](03-commands-and-authority.md)) | - |
| A client floods commands | rate limit, then drop | - |
| A client learns what it should not | interest filtering; no other player's personal view ([04](04-replication.md#interest)) | hide unseen sneakers |
| Eavesdropping or tampering on the path | **not prevented** on UDP: traffic is not encrypted (Steam P2P is carried by Steam). The password is not exposed (HMAC) | encrypt after the handshake (ECDH + AES-GCM, both in .NET's base library) |
| A malicious server | it can lie about the world and can make a client show anything; it cannot run code on the client. It never sends code or files | - |
| A crafted packet crashes a peer | every decoder bounds lengths and counts; a decode error drops that connection only | fuzz the decoders |
| Someone uses the server to amplify traffic | the first reply to an unknown address is smaller than the request | - |

### Disconnects

- Keep-alive every second; a peer silent for 15 s is gone (the old branch's timeout).
- A client that loses the server shows "Connection lost", tries to reconnect for 60 s with the same identity, and resumes with a new
  baseline. The player's characters stay in the world during that time ([07](07-settings-mods-saves.md#offline-players-and-bases)).
- The host closing the game ends an internal server: it saves, tells the clients, and they return to the menu. Nothing hands a world
  over to another player.

## What must not change in single-player

- No transport is opened, no thread started, Steam is started as today.
- `Meitou.Net` is not loaded until a session is opened or joined.

## Not checked

- Steam peer-to-peer between two accounts behind NAT (reported working by the owner; not recorded as verified).
- Whether Steam starts Kenshi instead of Meitou for a cold "Join game".
- Whether the Steam overlay draws over the Vulkan renderer (net.md: Unknown).
- Whether the library exports any auth-ticket or lobby function (the export table was not re-read for this design).
- LiteNetLib's current version and behaviour on .NET 11.

## Corrections from the code check (19 November 2026)

Every statement of fact in this file was checked against the code at `a1ef2e06f`. The rows below did not hold as written: where a row
and the text above differ, the row is right. Line numbers are this file's. WRONG: the code does something else. OFF: the right
thing at another place or with another number, or cited lines that do not say it. UNSUPPORTED: no code found that backs it.

| Line | Statement | Verdict | What the code says | Corrected |
|---|---|---|---|---|
| 28-29 | Data loads once per process, so a different list needs a restart (`GameDatabase.cs:91`) | UNSUPPORTED | `Data/GameDatabase.cs:91` is the static factory `GameDatabase.Load(LoadOrder, edit)`; nothing there or in its docs says once per process. It is called from `Game/Mods/ModDataPhase.cs:40` (`LoadData`), `Game/Rendering/Frame/WorldFrame.cs:57`, `Game/Host/Program.cs:156` (`--data-check`), `Game/DevTools/WikiExport.cs:61`. The game makes one database at boot (WorldFrame/ModDataPhase), so a restart is how a list changes in practice, but no cited code enforces it. | "The game builds its `GameDatabase` once at boot (`ModDataPhase.LoadData`, `WorldFrame.cs:57`) and nothing reloads it while running, so another mod list means a restart." (verify the 'nothing reloads' by a search of callers before keeping it) |
| 24-26 | The navmesh cache key uses write times and absolute paths | OFF | `Sim/Navigation/NavContentHash.cs:144-149` stamps file name + length + `LastWriteTimeUtc` (no path). Only collision files add the resolved path as text (`:111`, `Add(file)`) and mod folders' layer names (`:81`). Write times are right. | "...uses each file's name, size and write time, plus the resolved path of collision files (`NavContentHash.cs:111,144-149`), so it differs between machines." |
| 127-130 | Missing Workshop mods: "Subscribe and download N mods" through the existing Workshop subscribe | OFF | The existing flow is launcher-only: the child process `WorkshopSubscribeChild`/`WorkshopSubscribeRunner` live in `launcher/Meitou.Launcher/` (docs/launcher.md:363-365, killed after 4 min, "Steam is not started", library from `SteamLibrary.Locate`). Reusable from the game: `Core/Steam/WorkshopSubscribe.Run(libraryPath, ids)` (`WorkshopSubscribe.cs:35`), by id, Windows and macOS. The game process has its own `SteamSession`. | "Core's `WorkshopSubscribe.Run(libraryPath, ids)` subscribes by id; today only the launcher calls it, in a child process (docs/launcher.md:349-378). The game would call it or start such a child." |
