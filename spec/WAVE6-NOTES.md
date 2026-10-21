# Wave 6 as built (on `multiplayer`: G2 27a1a3f, G3 7cc1d45, G4 ba0d9bc; version bump a5ba017)

Built and unit-tested on Linux without the game or a GPU: none of G2/G3 has been run. Upstream master had no new commits.

## G2 host mode (`GameHost.Hosting.cs`)

- `HostGame` stores `hostPending` and, for a new game / save, sets `newGameRequested` / `loadRequestedFolder` (the usual flows);
  `HostFrameCore` (called from `NetEndFrame`) starts hosting on the first `HostReady` frame. Hosting the running game with
  none running is refused with a notice. A failed scene load clears `hostPending` (`GameHost.Transition.cs`).
- `HostGameLink : IHostGame`: `Gate` (cheats, `Placeable = PlayerInterface.PlanPlaceable`, new in `PlayerInterface.Build.cs`:
  placement rules against the sites without characters or queued ghosts; walls pass), `Rules = CurrentRules()` from the config,
  `Weather()` cached per `WorldWeather.Revision` or 1 s.
- `WorldWeather.Revision` moves on `Restore`, `Choose` and a forced weather change seen in `Update`.
- `RecordingAppearances` wraps `GeneratedAppearances` in `BootSimulation` (every run that has a population).
- Saving: `LocalPlayer = config.PlayerId` while hosting; clients refused F5/F9/menu save-load; `The host saved the game.` after
  a finished write (flag set by the worker). Main menu and quitting stop hosting (`ShutdownNet()` after the loop).
- Deviation: D4's separate `GameHost.Time.cs`, `TimeRequest`, `HostOutputs` are not used (contract C8/C10: NetHost owns clock and tap).

## G3 client mode (`GameHost.Client.cs`)

- **Deviation**: connecting and the handshake are not scene-load stages. U2's window treats "closed while not Loading" as a
  cancel and a covering load would fight it, so the "connect"/"host" waits run per frame (60 s for the first WorldReset) and the
  scene load ("world", "people" ≤ 60 s, "finishing") starts at each WorldReset.
- `SettleNewWorld` runs no tick on a replica; `ResumeAfterCreation` neither; warm-up readiness on a replica = `HasPresented`;
  blood luckyren from `SessionRules`; `HandleNewGame` refused while a client; `HandleMainMenu` while a client leaves.
- Relations: the host's player row is copied into the client data's other factions' entries towards the player faction, only when
  the host's player faction equals the data's `PlayerFaction`; restored on leave.
- A failed join before any replica keeps the ended `NetSessionView` on `player.Net` for 3 frames so U2's window shows the reason,
  plus a notice.
- Nav zones are unloaded when a replica is built; `UpdateNav` stays idle while `population` is null.

## G4 command line and logs

`GameOptions`: `--host [port]` (non-digit next arg is not a port; overflow → -1 → refused), `--join`, `--name`, `--password`,
`--config`; `NetProblem(screenshot)` (tested, `Ui/MultiplayerOptionsTests.cs`, 11 cases); `Program` uses `LoadOrCreate` for
`--config` and sets `HostOnStart`/`JoinOnStart`/`NameOverride` on the interactive GameHost; `--join` boots to the title (C36) and
joins on the first uncovered frame; `--host` sets `hostPending` then. `GameLog`: log.txt, then log.2.txt..log.4.txt.

## Also done

- `NetProtocol.Version` = 2 (a5ba017). The natural-rock gate bound was done by 196ff29 (other session).
- `NetClient`: a Hello the codec refuses (data set over 1024 files) ends the join with `Data`, "Too many data files and mods to
  join a game." instead of throwing out of `BeginFrame`.

## Needs a real two-instance run (wave 7)

Everything in G1-G4 touching the window, renderer, audio, weather, editor and LiteNetLib over real UDP. See PROGRESS.md checklist.
