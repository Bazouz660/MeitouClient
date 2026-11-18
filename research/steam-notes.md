# Steam under Kenshi's own library

MeitouClient runs on Steam as Kenshi (app 233860) and loads the `steam_api64.dll` of the player's Kenshi install; it ships no Steam file.
On master only the Workshop calls are bound (`src/Meitou.Core/Steam/SteamUgc.cs`, `src/Meitou.Game/Host/SteamSession.cs`).

The October 2026 test branch (`multiplayer` on the fork) recorded what that library offers, in its `docs/net.md` (section on
`Transport/Steam`). Read it there. In short:

- The library is Steamworks of about SDK 1.37 to 1.41. It has no `ISteamNetworkingSockets` and no `ISteamNetworkingMessages`.
- It has the older peer-to-peer API (`ISteamNetworking`, version `SteamNetworking005`: send, read, accept a session, allow relaying) and
  the friends API with rich presence, including the "Join game" request (callback 337).
- Callbacks need a small native shim laid out as the library's callback base class; the notes describe it.
- Marked verified there on a running Steam client: start as app 233860 from our process, the user's id and name, the friends list, rich
  presence set and read back, callbacks delivered through the shim.
- The owner reports that joining a friend through Steam worked on that branch.

Consequences for the design: a friend is joined through the old peer-to-peer API with relaying, with no port to open; a dedicated server
has no Steam user and is reached by address; the library exists on Windows only (Kenshi ships no other).
